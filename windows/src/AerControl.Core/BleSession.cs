namespace AerControl.Core;

/// <summary>Platform BLE connection primitive (one peripheral at a time).</summary>
public interface IBleLink : IDisposable
{
    bool IsConnected { get; }
    /// <summary>Raised when the link drops without <see cref="DisconnectAsync"/> being called.</summary>
    event EventHandler<string?>? ConnectionLost;
    event EventHandler<byte[]>? NotificationReceived;
    /// <summary>Connects, discovers the aer service/characteristic and enables notifications.</summary>
    Task ConnectAsync(ulong address, CancellationToken ct);
    /// <summary>Writes using <paramref name="mode"/>; returns the write type that succeeded.</summary>
    Task<string> WriteAsync(byte[] data, WriteMode mode, CancellationToken ct);
    Task DisconnectAsync();
    Task<IReadOnlyList<string>> DescribeGattAsync(CancellationToken ct);
}

public interface IBleScanner
{
    bool IsScanning { get; }
    event EventHandler<Advertisement>? AdvertisementReceived;
    event EventHandler? Stopped;
    void Start(bool serviceFilterInHardware);
    void Stop();
}

public interface IBluetoothRadio
{
    Task<AdapterState> GetStateAsync();
    event EventHandler<AdapterState>? StateChanged;
}

/// <summary>
/// Connection policy on top of <see cref="IBleLink"/>: serialized sends, connect-on-demand,
/// a post-write reply/settle window, idle disconnect (so the phone app can connect), and
/// auto-reconnect with backoff. Mirrors BLEService in the macOS client.
/// </summary>
public sealed class BleSession : IDisposable
{
    private readonly IBleLink _link;
    private readonly IBluetoothRadio _radio;
    private readonly FileLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private Timer? _idleTimer;
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempt;
    private DateTime _lastActivityUtc = DateTime.MinValue;
    private TaskCompletionSource<byte[]>? _reply;
    private AdapterState _adapter = AdapterState.Unknown;
    private LinkStatus _status = LinkStatus.NoDevice;
    private bool _disposed;

    public BleSession(IBleLink link, IBluetoothRadio radio, FileLogger log)
    {
        _link = link;
        _radio = radio;
        _log = log;
        _link.ConnectionLost += OnConnectionLost;
        _link.NotificationReceived += OnNotification;
        _radio.StateChanged += (_, s) => { _adapter = s; RefreshStatus(); };
    }

    public LinkStatus Status => _status;
    public event EventHandler<LinkStatus>? StatusChanged;
    public event EventHandler<byte[]>? NotificationReceived;

    public ulong? TargetAddress { get; private set; }
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public bool AutoReconnect { get; set; } = true;
    public WriteMode WriteMode { get; set; } = WriteMode.Automatic;
    /// <summary>Listen window after a write. Also keeps a write-without-response from being
    /// dropped by an immediate disconnect (the first live test failed exactly that way).</summary>
    public TimeSpan ReplyWindow { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>Base delay for reconnect backoff (doubles per attempt, capped at 60 s).</summary>
    public TimeSpan ReconnectBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public async Task InitializeAsync()
    {
        _adapter = await _radio.GetStateAsync().ConfigureAwait(false);
        RefreshStatus();
    }

    public async Task SelectDeviceAsync(ulong? address)
    {
        if (TargetAddress == address) return;
        TargetAddress = address;
        if (_link.IsConnected) await DisconnectAsync("device changed").ConfigureAwait(false);
        RefreshStatus();
    }

    // ---- Sending ---------------------------------------------------------------------

    public async Task<WriteOutcome> SendAsync(byte[] data, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _reply, tcs);
            var mode = await _link.WriteAsync(data, WriteMode, ct).ConfigureAwait(false);
            _log.Info($"wrote {data.Length}B ({mode}): {Protocol.Hex.ToHex(data)}");

            byte[]? reply = null;
            var first = await Task.WhenAny(tcs.Task, Task.Delay(ReplyWindow, ct)).ConfigureAwait(false);
            if (first == tcs.Task && tcs.Task.IsCompletedSuccessfully) reply = tcs.Task.Result;
            return new WriteOutcome(mode, reply);
        }
        finally
        {
            Volatile.Write(ref _reply, null);
            Touch();
            _gate.Release();
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureConnectedAsync(ct).ConfigureAwait(false); }
        finally { Touch(); _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> InspectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await _link.DescribeGattAsync(ct).ConfigureAwait(false);
        }
        finally { Touch(); _gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_link.IsConnected) return;

        _adapter = await _radio.GetStateAsync().ConfigureAwait(false);
        if (_adapter == AdapterState.Unsupported) throw new BleException(BleErrorKind.Unsupported);
        if (_adapter != AdapterState.On) { RefreshStatus(); throw new BleException(BleErrorKind.BluetoothOff); }
        if (TargetAddress is not ulong address) throw new BleException(BleErrorKind.NoDeviceSelected);

        SetStatus(LinkStatus.Connecting);
        _log.Info($"connecting to {BluetoothAddress.Format(address)}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await _link.ConnectAsync(address, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await SafeDisconnectLinkAsync().ConfigureAwait(false);
            RefreshStatus();
            throw new BleException(BleErrorKind.Timeout, "connect");
        }
        catch
        {
            await SafeDisconnectLinkAsync().ConfigureAwait(false);
            RefreshStatus();
            throw;
        }
        _reconnectAttempt = 0;
        _log.Info("connected and ready");
        SetStatus(LinkStatus.Connected);
    }

    // ---- Disconnect / idle / reconnect -------------------------------------------------

    public async Task DisconnectAsync(string reason = "user")
    {
        CancelReconnect();
        StopIdleTimer();
        if (_link.IsConnected)
        {
            _log.Info($"disconnecting ({reason})");
            await SafeDisconnectLinkAsync().ConfigureAwait(false);
        }
        RefreshStatus();
    }

    private async Task SafeDisconnectLinkAsync()
    {
        try { await _link.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn($"disconnect error: {ex.Message}"); }
    }

    private void Touch()
    {
        _lastActivityUtc = DateTime.UtcNow;
        StopIdleTimer();
        if (IdleTimeout <= TimeSpan.Zero || !_link.IsConnected) return;
        lock (_sync)
        {
            _idleTimer = new Timer(_ => _ = IdleDisconnectAsync(), null, IdleTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task IdleDisconnectAsync()
    {
        // Take the gate so an idle disconnect never cuts a send in half.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow - _lastActivityUtc < IdleTimeout || !_link.IsConnected) return;
            _log.Info($"disconnecting (idle {IdleTimeout.TotalSeconds:0}s)");
            await SafeDisconnectLinkAsync().ConfigureAwait(false);
            RefreshStatus();
        }
        finally { _gate.Release(); }
    }

    private void StopIdleTimer()
    {
        lock (_sync)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    private void OnConnectionLost(object? sender, string? why)
    {
        _log.Warn($"connection lost: {why ?? "no reason"}");
        Volatile.Read(ref _reply)?.TrySetException(new BleException(BleErrorKind.Disconnected, why));
        StopIdleTimer();
        RefreshStatus();
        MaybeReconnect();
    }

    /// <summary>
    /// Reconnect after an unexpected drop only while the link was in use: always in
    /// stay-connected mode, otherwise only within the idle window. That way an idle app
    /// doesn't grab the device back from the phone.
    /// </summary>
    private void MaybeReconnect()
    {
        if (!AutoReconnect || TargetAddress is null || _disposed) return;
        bool inUse = IdleTimeout <= TimeSpan.Zero || DateTime.UtcNow - _lastActivityUtc < IdleTimeout;
        if (!inUse) return;

        int attempt = Interlocked.Increment(ref _reconnectAttempt);
        var delay = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(60).Ticks,
            ReconnectBaseDelay.Ticks * (1L << Math.Min(attempt, 6))));
        _log.Info($"auto-reconnect attempt {attempt} in {delay.TotalSeconds:0.#}s");

        CancelReconnect();
        var cts = new CancellationTokenSource();
        lock (_sync) _reconnectCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await ConnectAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Warn($"auto-reconnect failed: {ex.Message}");
                MaybeReconnect();
            }
        });
    }

    private void CancelReconnect()
    {
        lock (_sync)
        {
            _reconnectCts?.Cancel();
            _reconnectCts = null;
        }
    }

    private void OnNotification(object? sender, byte[] value)
    {
        _log.Info($"notify: {Protocol.Hex.ToHex(value)}");
        Volatile.Read(ref _reply)?.TrySetResult(value);
        NotificationReceived?.Invoke(this, value);
    }

    // ---- Status ------------------------------------------------------------------------

    private void RefreshStatus()
    {
        var s = _adapter switch
        {
            AdapterState.Off => LinkStatus.BluetoothOff,
            AdapterState.Unsupported => LinkStatus.Unsupported,
            _ when TargetAddress is null => LinkStatus.NoDevice,
            _ => _link.IsConnected ? LinkStatus.Connected : LinkStatus.Disconnected,
        };
        SetStatus(s);
    }

    private void SetStatus(LinkStatus s)
    {
        if (_status == s) return;
        _status = s;
        StatusChanged?.Invoke(this, s);
    }

    public void Dispose()
    {
        _disposed = true;
        CancelReconnect();
        StopIdleTimer();
        _link.Dispose();
    }
}
