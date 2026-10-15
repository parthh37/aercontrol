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

    public bool IsConnected => _connected && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public event EventHandler<string?>? ConnectionLost;
    public event EventHandler<byte[]>? NotificationReceived;

    public async Task ConnectAsync(ulong address, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false)
                  ?? throw new BleException(BleErrorKind.DeviceNotFound);
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // GattSession.MaintainConnection keeps the link up between writes instead of
        // letting Windows drop it as soon as no GATT operation is pending.
        _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct).ConfigureAwait(false);
        _session.MaintainConnection = true;

        _service = await FindServiceAsync(_device, ct).ConfigureAwait(false);
        _characteristic = await FindCharacteristicAsync(_service, AerGatt.Characteristic, ct).ConfigureAwait(false)
                          ?? throw new BleException(BleErrorKind.CharacteristicNotFound);

        log.Info($"characteristic properties: {_characteristic.CharacteristicProperties}");

        // Replies (spray ack, status) arrive on the RESPONSE characteristic. Notify stays on
        // for the command characteristic too, which was part of what made sprays reliable.
        _response = await FindCharacteristicAsync(_service, AerGatt.Response, ct).ConfigureAwait(false);
        foreach (var c in new[] { _characteristic, _response })
            if (c is not null) await EnableNotificationsAsync(c, ct).ConfigureAwait(false);
        _connected = true;
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

    private async Task EnableNotificationsAsync(GattCharacteristic c, CancellationToken ct)
    {
        var props = c.CharacteristicProperties;
        if (!props.HasFlag(GattCharacteristicProperties.Notify) && !props.HasFlag(GattCharacteristicProperties.Indicate)) return;
        c.ValueChanged += OnValueChanged;
        var cccd = props.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;
        var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(cccd).AsTask(ct).ConfigureAwait(false);
        if (status == GattCommunicationStatus.Success) log.Info($"notifications on for {c.Uuid}");
        else log.Warn($"couldn't enable notifications on {c.Uuid}: {status}");
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
        // WinRT has no explicit "disconnect". The link closes once every GATT object and
        // the device are disposed and MaintainConnection is off.
        _connected = false;
        if (_characteristic is not null) _characteristic.ValueChanged -= OnValueChanged;
        if (_response is not null) _response.ValueChanged -= OnValueChanged;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        if (_session is not null)
        {
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
        return Task.CompletedTask;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && _connected)
        {
            _connected = false;
            ConnectionLost?.Invoke(this, "link dropped");
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        NotificationReceived?.Invoke(this, args.CharacteristicValue.ToArray());

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
