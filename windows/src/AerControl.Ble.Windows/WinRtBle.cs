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

        var services = await _device.GetGattServicesForUuidAsync(AerGatt.Service, BluetoothCacheMode.Uncached)
            .AsTask(ct).ConfigureAwait(false);
        if (services.Status == GattCommunicationStatus.Unreachable)
            throw new BleException(BleErrorKind.Unreachable);
        if (services.Status != GattCommunicationStatus.Success)
            throw new BleException(BleErrorKind.Disconnected, $"service discovery: {services.Status}");
        _service = services.Services.FirstOrDefault() ?? throw new BleException(BleErrorKind.ServiceNotFound);

        var chars = await _service.GetCharacteristicsForUuidAsync(AerGatt.Characteristic, BluetoothCacheMode.Uncached)
            .AsTask(ct).ConfigureAwait(false);
        if (chars.Status != GattCommunicationStatus.Success)
            throw new BleException(BleErrorKind.Disconnected, $"characteristic discovery: {chars.Status}");
        _characteristic = chars.Characteristics.FirstOrDefault()
                          ?? throw new BleException(BleErrorKind.CharacteristicNotFound);

        log.Info($"characteristic properties: {_characteristic.CharacteristicProperties}");

        // Replies (spray ack, status) arrive on the RESPONSE characteristic. Notify stays on
        // for the command characteristic too, which was part of what made sprays reliable.
        var responses = await _service.GetCharacteristicsForUuidAsync(AerGatt.Response, BluetoothCacheMode.Uncached)
            .AsTask(ct).ConfigureAwait(false);
        _response = responses.Status == GattCommunicationStatus.Success ? responses.Characteristics.FirstOrDefault() : null;
        foreach (var c in new[] { _characteristic, _response })
            if (c is not null) await EnableNotificationsAsync(c, ct).ConfigureAwait(false);
        _connected = true;
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
