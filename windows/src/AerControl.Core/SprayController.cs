using AerControl.Protocol;

namespace AerControl.Core;

/// <summary>
/// Orchestrates a spray: BLE write, mN bookkeeping, history and the change event the UI
/// and widget provider listen to. The window, tray, widget, scheduler, hotkey and CLI all
/// call <see cref="SprayAsync"/>.
/// </summary>
public sealed class SprayController
{
    private readonly JsonFileStore<AerSettings> _settingsStore;
    private readonly JsonFileStore<List<SprayRecord>> _historyStore;
    private readonly JsonFileStore<List<DeviceStatus>> _statusStore;
    private readonly object _lock = new();
    private List<SprayRecord> _history;
    private AerSettings _settings;
    private int _spraying;

    public const int HistoryLimit = 200;

    public SprayController(BleSession session, FileLogger log, string settingsPath, string historyPath)
    {
        Session = session;
        Log = log;
        _settingsStore = new JsonFileStore<AerSettings>(settingsPath);
        _historyStore = new JsonFileStore<List<SprayRecord>>(historyPath);
        _settings = _settingsStore.Load();
        _history = _historyStore.Load();
        _statusStore = new JsonFileStore<List<DeviceStatus>>(
            Path.Combine(Path.GetDirectoryName(settingsPath) ?? ".", "status.json"));
        DeviceStatus = _statusStore.Load().FirstOrDefault();
        Session.StatusChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public BleSession Session { get; }
    public FileLogger Log { get; }

    /// <summary>Raised on any thread when settings, history or link status change.</summary>
    public event EventHandler? Changed;

    /// <summary>A snapshot copy; change settings through <see cref="UpdateSettingsAsync"/>.</summary>
    public AerSettings Settings { get { lock (_lock) return _settings.Clone(); } }

    public IReadOnlyList<SprayRecord> History { get { lock (_lock) return _history.ToList(); } }
    public SprayRecord? LastSpray { get { lock (_lock) return _history.FirstOrDefault(); } }
    public bool IsSpraying => Volatile.Read(ref _spraying) > 0;

    /// <summary>Read battery/refill right after each successful spray, while still connected.</summary>
    public bool RefreshStatusAfterSpray { get; set; } = true;

    /// <summary>Battery / refill as last reported by the device (persisted across launches).</summary>
    public DeviceStatus? DeviceStatus { get; private set; }

    /// <summary>Refreshes when the cached reading is older than <paramref name="maxAge"/> (e.g. a window opens).</summary>
    public void RefreshDeviceStatusIfOlder(TimeSpan maxAge)
    {
        if (Settings.DeviceAddress is null || IsSpraying) return;
        if (DeviceStatus is { } s && DateTimeOffset.Now - s.UpdatedAt < maxAge) return;
        _ = RefreshDeviceStatusAsync();
    }

    /// <summary>Uses and advances the shared message counter, like a spray.</summary>
    private uint NextMessageNumber()
    {
        lock (_lock)
        {
            var mn = _settings.MessageNumber;
            if (_settings.AutoIncrementMessageNumber)
            {
                _settings.MessageNumber = MessageCounter.Next(mn);
                _settingsStore.Save(_settings);
            }
            return mn;
        }
    }

    /// <summary>Asks the device for battery and refill level (fetch status: mT 107, reply 157).</summary>
    public async Task<DeviceStatus?> RefreshDeviceStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var outcome = await Session.SendAsync(AerCommands.FetchStatus(NextMessageNumber()), ct).ConfigureAwait(false);
            if (outcome.Reply is not byte[] reply || DeviceStatusReport.Parse(reply) is not DeviceStatusReport r)
            {
                Log.Warn($"status: no fetch-status reply (got {(outcome.Reply is null ? "nothing" : Hex.ToHex(outcome.Reply))})");
                return null;
            }
            DeviceStatus = Core.DeviceStatus.From(r);
            _statusStore.Save(new List<DeviceStatus> { DeviceStatus });
            Log.Info($"status: battery {r.BatteryMillivolts} mV, {r.TotalSprays} sprays since refill, fw {r.FirmwareVersion}");
            Changed?.Invoke(this, EventArgs.Empty);
            return DeviceStatus;
        }
        catch (Exception ex)
        {
            Log.Warn($"status refresh failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Tells the device a fresh refill went in (mT 105, rR:true), resetting its counters.</summary>
    public async Task ResetRefillAsync(CancellationToken ct = default)
    {
        var outcome = await Session.SendAsync(AerCommands.Refill(NextMessageNumber()), ct).ConfigureAwait(false);
        if (outcome.Reply is byte[] b && AerReply.Parse(b) is { } reply
            && reply.MessageType == AerMessageType.Refill.ResponseType() && !reply.IsSuccess)
            throw new BleException(BleErrorKind.WriteFailed, $"device rejected the refill reset (ack {reply.Acknowledgement})");
        Log.Info("refill reset sent");
        await RefreshDeviceStatusAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The device acks a spray on the RESPONSE characteristic with {mT:154, ack:200}. A
    /// missing ack (older firmware, lost notification) still counts as sent.
    /// </summary>
    public static SprayRecord RecordFor(WriteOutcome outcome, SprayTrigger trigger, uint messageNumber)
    {
        var hex = outcome.Reply is null ? null : Hex.ToHex(outcome.Reply);
        var ack = outcome.Reply is null ? null : AerReply.Parse(outcome.Reply);
        if (ack is null || ack.MessageType != AerMessageType.SprayNow.ResponseType())
            return new SprayRecord { Success = true, Trigger = trigger, MessageNumber = messageNumber, WriteMode = outcome.Mode, Reply = hex };
        if (ack.IsSuccess)
            return new SprayRecord { Success = true, Trigger = trigger, MessageNumber = messageNumber, WriteMode = $"{outcome.Mode}, confirmed", Reply = hex };
        return new SprayRecord
        {
            Success = false, Trigger = trigger, MessageNumber = messageNumber, WriteMode = outcome.Mode, Reply = hex,
            Error = $"Device rejected the spray (ack {ack.Acknowledgement}).",
        };
    }

    public async Task InitializeAsync()
    {
        await Session.InitializeAsync().ConfigureAwait(false);
        await ApplyToSessionAsync().ConfigureAwait(false);
    }

    public async Task UpdateSettingsAsync(Action<AerSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_settings);
            _settingsStore.Save(_settings);
        }
        await ApplyToSessionAsync().ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyToSessionAsync()
    {
        var s = Settings;
        Session.IdleTimeout = TimeSpan.FromSeconds(Math.Max(0, s.IdleDisconnectSeconds));
        Session.AutoReconnect = s.AutoReconnect;
        Session.WriteMode = s.WriteMode;
        await Session.SelectDeviceAsync(s.DeviceAddress).ConfigureAwait(false);
    }

    public Task SelectDeviceAsync(DiscoveredDevice device)
    {
        Log.Info($"selected device {device.Name} [{device.AddressText}]");
        return UpdateSettingsAsync(s =>
        {
            s.DeviceAddress = device.Address;
            s.DeviceName = device.Name;
        });
    }

    public async Task ForgetDeviceAsync()
    {
        await Session.DisconnectAsync("forget device").ConfigureAwait(false);
        await UpdateSettingsAsync(s =>
        {
            s.DeviceAddress = null;
            s.DeviceName = null;
        }).ConfigureAwait(false);
    }

    /// <param name="rawPayload">Overrides the CBOR payload (CLI protocol experiments).</param>
    public async Task<SprayRecord> SprayAsync(SprayTrigger trigger, byte[]? rawPayload = null,
                                              CancellationToken ct = default)
    {
        Interlocked.Increment(ref _spraying);
        Changed?.Invoke(this, EventArgs.Empty);
        uint mn;
        lock (_lock) mn = _settings.MessageNumber;
        var data = rawPayload ?? new SprayPayload(MessageNumber: mn).Encode();
        Log.Info($"spray requested via {trigger}, mN={mn}");

        SprayRecord record;
        try
        {
            var outcome = await Session.SendAsync(data, ct).ConfigureAwait(false);
            record = RecordFor(outcome, trigger, mn);
            lock (_lock)
            {
                if (_settings.AutoIncrementMessageNumber)
                {
                    _settings.MessageNumber = MessageCounter.Next(mn);
                    _settingsStore.Save(_settings);
                }
            }
        }
        catch (Exception ex)
        {
            // A spray is a UI boundary: every failure becomes a history entry rather than a crash.
            Log.Error($"spray failed: {ex.Message}");
            record = new SprayRecord { Success = false, Trigger = trigger, MessageNumber = mn, Error = ex.Message };
        }

        lock (_lock)
        {
            _history.Insert(0, record);
            if (_history.Count > HistoryLimit) _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
            _historyStore.Save(_history);
        }
        Interlocked.Decrement(ref _spraying);
        Changed?.Invoke(this, EventArgs.Empty);
        // The link is still up: refresh counters and battery while connected.
        if (record.Success && rawPayload is null && RefreshStatusAfterSpray)
            _ = RefreshDeviceStatusAsync(CancellationToken.None);
        return record;
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _history.Clear();
            _historyStore.Save(_history);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
