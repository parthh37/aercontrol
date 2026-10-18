using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using AerControl.Core;
using AerControl.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;

namespace AerControl.Ble.Windows;

/// <summary>Creates the Windows implementations used by the CLI and the WinUI app.</summary>
public static class WinRtBle
{
    public static (BleSession Session, ScanSession Scan) Create(FileLogger log)
    {
        var session = new BleSession(new WinRtBleLink(log), new WinRtRadio(), log);
        var scan = new ScanSession(new WinRtBleScanner());
        return (session, scan);
    }
}

public sealed class WinRtBleLink(FileLogger log) : IBleLink
{
    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattDeviceService? _service;
    private GattCharacteristic? _characteristic;
    private GattCharacteristic? _response;
    private volatile bool _connected;
    private readonly object _release = new();
    private readonly ConcurrentDictionary<Guid, int> _notifications = new();
    private volatile string _responseSubscription = "not tried";

    public bool IsConnected => _connected && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public event EventHandler<string?>? ConnectionLost;
    public event EventHandler<byte[]>? NotificationReceived;

    public string Diagnostics
    {
        get
        {
            string mtu;
            try { mtu = _session?.MaxPduSize.ToString() ?? "?"; }
            catch (ObjectDisposedException) { mtu = "?"; }
            var seen = _notifications.IsEmpty ? "none"
                : string.Join(", ", _notifications.Select(kv => $"{Short(kv.Key)}×{kv.Value}"));
            return $"ATT MTU {mtu}; reply characteristic subscription: {_responseSubscription}; " +
                   $"notifications since connecting: {seen}";
        }
    }

    private static string Short(Guid uuid) => uuid.ToString()[..8];

    public async Task<int> WaitForMtuExchangeAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            int mtu;
            try { mtu = _session?.MaxPduSize ?? BleSession.DefaultAttMtu; }
            catch (ObjectDisposedException) { return BleSession.DefaultAttMtu; }
            if (mtu > BleSession.DefaultAttMtu || DateTime.UtcNow >= deadline)
            {
                if (sw.ElapsedMilliseconds >= 50) log.Info($"waited {sw.ElapsedMilliseconds} ms for the MTU exchange: ATT MTU {mtu}");
                return mtu;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    public async Task ConnectAsync(ulong address, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);
        _notifications.Clear();
        _responseSubscription = "not tried";

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false)
                  ?? throw new BleException(BleErrorKind.DeviceNotFound);
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // GattSession.MaintainConnection keeps the link up between writes instead of
        // letting Windows drop it as soon as no GATT operation is pending.
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
        _session.MaintainConnection = true;
        _session.MaxPduSizeChanged += OnMaxPduSizeChanged;

        _service = await FindServiceAsync(_device, ct).ConfigureAwait(false);
        _characteristic = await FindCharacteristicAsync(_service, AerGatt.Characteristic, ct).ConfigureAwait(false)
                          ?? throw new BleException(BleErrorKind.CharacteristicNotFound);

        log.Info($"characteristic properties: {_characteristic.CharacteristicProperties}");

        // Replies (spray ack, status) arrive on the RESPONSE characteristic. Notify stays on
        // for the command characteristic too, which was part of what made sprays reliable.
        _response = await FindCharacteristicAsync(_service, AerGatt.Response, ct).ConfigureAwait(false);
        if (_response is null) log.Warn("response characteristic not found; battery/refill status won't be readable");
        else log.Info($"response characteristic properties: {_response.CharacteristicProperties}");
        await EnableNotificationsAsync(_characteristic, ct).ConfigureAwait(false);
        if (_response is null)
        {
            _responseSubscription = "characteristic not found";
        }
        else
        {
            _responseSubscription = await EnableNotificationsAsync(_response, ct).ConfigureAwait(false);
            if (_responseSubscription != "on")
                log.Info("expected: the aer's reply characteristic has no CCCD (macOS reports the same), " +
                         "yet Windows still delivers its notifications (sprays show as confirmed)");
        }
        // Service and characteristic lookups can be answered from Windows' GATT cache while
        // the aer is busy with another computer or the phone. Only report ready once the radio
        // link is really up, so the session can wait and retry instead of failing the write.
        if (!await WaitForLinkAsync(_device, ct).ConfigureAwait(false))
            throw new BleException(BleErrorKind.Unreachable);
        log.Info($"ATT MTU {_session.MaxPduSize}");
        _connected = true;
    }

    private static async Task<bool> WaitForLinkAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return true;
    }

    private static readonly BluetoothCacheMode[] CacheModes = [BluetoothCacheMode.Uncached, BluetoothCacheMode.Cached];
    private const int DiscoveryRounds = 4;

    /// <summary>
    /// Finds the aer service. A single uncached lookup isn't enough on Windows: right after
    /// connecting it often returns Success with an empty list (the link is still coming up),
    /// and some adapters only answer the full-table query, not the UUID-filtered one. So each
    /// round tries filtered then full, uncached then cached, with a growing pause in between.
    /// </summary>
    private async Task<GattDeviceService> FindServiceAsync(BluetoothLEDevice device, CancellationToken ct)
    {
        var lastStatus = GattCommunicationStatus.Success;
        for (int round = 1; round <= DiscoveryRounds; round++)
        {
            foreach (var mode in CacheModes)
            {
                var filtered = await device.GetGattServicesForUuidAsync(AerGatt.Service, mode).AsTask(ct).ConfigureAwait(false);
                lastStatus = filtered.Status;
                ThrowIfAccessDenied(filtered.Status);
                if (filtered.Status == GattCommunicationStatus.Success && Pick(filtered.Services) is { } hit) return hit;

                var all = await device.GetGattServicesAsync(mode).AsTask(ct).ConfigureAwait(false);
                ThrowIfAccessDenied(all.Status);
                if (all.Status != GattCommunicationStatus.Success)
                {
                    lastStatus = all.Status;
                    log.Warn($"service discovery round {round} ({mode}): {all.Status}");
                    continue;
                }
                log.Info($"service discovery round {round} ({mode}): " +
                         (all.Services.Count == 0 ? "no services" : string.Join(", ", all.Services.Select(s => s.Uuid))));
                if (Pick(all.Services) is { } found) return found;
            }
            if (round < DiscoveryRounds)
                await Task.Delay(TimeSpan.FromMilliseconds(500 * round), ct).ConfigureAwait(false);
        }
        if (lastStatus == GattCommunicationStatus.Unreachable) throw new BleException(BleErrorKind.Unreachable);
        throw new BleException(BleErrorKind.ServiceNotFound);
    }

    /// <summary>Returns the aer service from a result list and disposes the rest, so stray
    /// service handles don't keep the link open after we disconnect.</summary>
    private static GattDeviceService? Pick(IReadOnlyList<GattDeviceService> services)
    {
        GattDeviceService? match = null;
        foreach (var s in services)
        {
            if (match is null && s.Uuid == AerGatt.Service) match = s;
            else s.Dispose();
        }
        return match;
    }

    private async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid uuid, CancellationToken ct)
    {
        var lastStatus = GattCommunicationStatus.Success;
        foreach (var mode in CacheModes)
        {
            var r = await service.GetCharacteristicsForUuidAsync(uuid, mode).AsTask(ct).ConfigureAwait(false);
            lastStatus = r.Status;
            ThrowIfAccessDenied(r.Status);
            if (r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0) return r.Characteristics[0];

            var all = await service.GetCharacteristicsAsync(mode).AsTask(ct).ConfigureAwait(false);
            if (all.Status == GattCommunicationStatus.Success && all.Characteristics.FirstOrDefault(c => c.Uuid == uuid) is { } c)
                return c;
        }
        log.Warn($"characteristic {uuid} not found (last status {lastStatus})");
        if (lastStatus == GattCommunicationStatus.Unreachable) throw new BleException(BleErrorKind.Unreachable);
        return null;
    }

    private static void ThrowIfAccessDenied(GattCommunicationStatus status)
    {
        if (status == GattCommunicationStatus.AccessDenied)
            throw new BleException(BleErrorKind.Disconnected,
                "Windows denied Bluetooth access. Check Settings › Privacy & security › Other devices " +
                "(\"Communicate with unpaired devices\") and that AerControl is allowed");
    }

    private const int NotifyAttempts = 3;

    /// <summary>
    /// Subscribes to <paramref name="c"/>. The handler goes on first and stays on even when
    /// the CCCD write fails or the characteristic doesn't advertise Notify: CoreBluetooth hands
    /// over every notification it receives, but WinRT only raises ValueChanged for handlers that
    /// are attached, and on macOS the RESPONSE characteristic's notify setup errors while its
    /// replies still arrive. The CCCD write is retried because, like service discovery, it can
    /// fail while the link is still coming up.
    /// </summary>
    /// <returns>"on", or why the subscription failed (for <see cref="Diagnostics"/>).</returns>
    private async Task<string> EnableNotificationsAsync(GattCharacteristic c, CancellationToken ct)
    {
        c.ValueChanged += OnValueChanged;
        var props = c.CharacteristicProperties;
        if (!props.HasFlag(GattCharacteristicProperties.Notify) && !props.HasFlag(GattCharacteristicProperties.Indicate))
        {
            log.Info($"{c.Uuid} doesn't advertise notify ({props}); listening anyway");
            return $"no notify property ({props})";
        }
        var cccd = props.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        string why = "";
        for (int attempt = 1; attempt <= NotifyAttempts; attempt++)
        {
            try
            {
                var r = await c.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(cccd).AsTask(ct).ConfigureAwait(false);
                if (r.Status == GattCommunicationStatus.Success)
                {
                    log.Info($"notifications on for {c.Uuid}");
                    return "on";
                }
                why = $"{r.Status}{(r.ProtocolError is byte e ? $" (ATT error 0x{e:X2})" : "")}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                why = ex.Message;
            }
            log.Warn($"couldn't enable notifications on {c.Uuid} (attempt {attempt}/{NotifyAttempts}): {why}");
            if (attempt < NotifyAttempts) await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct).ConfigureAwait(false);
        }
        log.Info($"still listening on {c.Uuid} in case the device notifies anyway");
        return $"CCCD write failed: {why}";
    }

    public async Task<string> WriteAsync(byte[] data, WriteMode mode, CancellationToken ct)
    {
        var c = _characteristic ?? throw new BleException(BleErrorKind.Disconnected);
        var props = c.CharacteristicProperties;
        bool tryWithResponse = mode switch
        {
            WriteMode.WithResponse => true,
            WriteMode.WithoutResponse => false,
            _ => props.HasFlag(GattCharacteristicProperties.Write)
                 || !props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse),
        };

        if (tryWithResponse)
        {
            var r = await c.WriteValueWithResultAsync(data.AsBuffer(), GattWriteOption.WriteWithResponse)
                .AsTask(ct).ConfigureAwait(false);
            if (r.Status == GattCommunicationStatus.Success) return "with-response";
            var why = $"{r.Status}{(r.ProtocolError is byte e ? $" (ATT error 0x{e:X2})" : "")}";
            if (mode != WriteMode.Automatic) throw new BleException(BleErrorKind.WriteFailed, why);
            log.Warn($"write with response failed ({why}); falling back");
        }

        var r2 = await c.WriteValueWithResultAsync(data.AsBuffer(), GattWriteOption.WriteWithoutResponse)
            .AsTask(ct).ConfigureAwait(false);
        if (r2.Status != GattCommunicationStatus.Success)
            throw new BleException(BleErrorKind.WriteFailed, r2.Status.ToString());
        return "without-response";
    }

    public async Task<IReadOnlyList<string>> DescribeGattAsync(CancellationToken ct)
    {
        var device = _device ?? throw new BleException(BleErrorKind.Disconnected);
        var lines = new List<string>();
        var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
        foreach (var s in services.Services)
        {
            lines.Add($"service {s.Uuid.ToString().ToUpperInvariant()}");
            var chars = await s.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
            foreach (var c in chars.Characteristics)
                lines.Add($"  char {c.Uuid.ToString().ToUpperInvariant()}  [{c.CharacteristicProperties}]");
        }
        return lines;
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        Release(only: null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// WinRT has no explicit "disconnect". The link closes once every GATT object and the
    /// device are disposed and MaintainConnection is off. <paramref name="only"/> limits the
    /// release to that device, so a late drop event can't tear down a newer connection.
    /// </summary>
    private void Release(BluetoothLEDevice? only)
    {
        lock (_release)
        {
            if (only is not null && !ReferenceEquals(only, _device)) return;
            if (_characteristic is not null) _characteristic.ValueChanged -= OnValueChanged;
            if (_response is not null) _response.ValueChanged -= OnValueChanged;
            if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            if (_session is not null)
            {
                _session.MaxPduSizeChanged -= OnMaxPduSizeChanged;
                _session.MaintainConnection = false;
                _session.Dispose();
            }
            _service?.Dispose();
            _device?.Dispose();
            _characteristic = null;
            _response = null;
            _service = null;
            _session = null;
            _device = null;
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || !_connected) return;
        _connected = false;
        // Left alive, the GattSession's MaintainConnection makes Windows reconnect on its own
        // and hold the aer indefinitely while this app shows "disconnected", which locks other
        // computers and the phone out. Release everything (off this WinRT callback).
        _ = Task.Run(() => Release(only: sender));
        ConnectionLost?.Invoke(this, "link dropped");
    }

    private void OnMaxPduSizeChanged(GattSession sender, object args) => log.Info($"ATT MTU now {sender.MaxPduSize}");

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var value = args.CharacteristicValue.ToArray();
        _notifications.AddOrUpdate(sender.Uuid, 1, (_, n) => n + 1);
        log.Info($"notification on {sender.Uuid} ({value.Length}B)");
        NotificationReceived?.Invoke(this, value);
    }

    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();
}

public sealed class WinRtBleScanner : IBleScanner
{
    private BluetoothLEAdvertisementWatcher? _watcher;

    public bool IsScanning => _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    public event EventHandler<Advertisement>? AdvertisementReceived;
    public event EventHandler? Stopped;

    public void Start(bool serviceFilterInHardware)
    {
        Stop();
        var w = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        if (serviceFilterInHardware) w.AdvertisementFilter.Advertisement.ServiceUuids.Add(AerGatt.Service);
        w.Received += (_, e) =>
        {
            bool hasService = e.Advertisement.ServiceUuids.Contains(AerGatt.Service);
            AdvertisementReceived?.Invoke(this, new Advertisement(
                e.BluetoothAddress, e.Advertisement.LocalName, e.RawSignalStrengthInDBm, hasService));
        };
        w.Stopped += (_, _) => Stopped?.Invoke(this, EventArgs.Empty);
        _watcher = w;
        w.Start();
    }

    public void Stop()
    {
        if (_watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Created })
            _watcher.Stop();
        _watcher = null;
    }
}

public sealed class WinRtRadio : IBluetoothRadio
{
    private Radio? _radio;
    public event EventHandler<AdapterState>? StateChanged;

    public async Task<AdapterState> GetStateAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsLowEnergySupported) return AdapterState.Unsupported;
        if (_radio is null)
        {
            _radio = await adapter.GetRadioAsync();
            if (_radio is not null)
                _radio.StateChanged += (r, _) => StateChanged?.Invoke(this, Map(r.State));
        }
        return _radio is null ? AdapterState.Unknown : Map(_radio.State);
    }

    private static AdapterState Map(RadioState s) => s == RadioState.On ? AdapterState.On : AdapterState.Off;
}
