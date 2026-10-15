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
            record = new SprayRecord
            {
                Success = true, Trigger = trigger, MessageNumber = mn, WriteMode = outcome.Mode,
                Reply = outcome.Reply is null ? null : Hex.ToHex(outcome.Reply),
            };
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
