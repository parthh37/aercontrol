namespace AerControl.Core;

/// <summary>
/// Merges raw advertisements into a device list. With active scanning, the name usually
/// arrives in a separate scan-response packet, so filtering happens after the merge.
/// </summary>
public sealed class ScanSession
{
    private readonly IBleScanner _scanner;
    private readonly Dictionary<ulong, DiscoveredDevice> _all = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _stopCts;

    public ScanSession(IBleScanner scanner)
    {
        _scanner = scanner;
        _scanner.AdvertisementReceived += OnAdvertisement;
        _scanner.Stopped += (_, _) => ScanningChanged?.Invoke(this, false);
    }

    public ScanFilter Filter { get; set; } = ScanFilter.ServiceOrName;
    public string NameFilter { get; set; } = ScanMatcher.DefaultNameFilter;
    public bool IsScanning => _scanner.IsScanning;

    /// <summary>Raised (on a background thread) whenever the filtered list changes.</summary>
    public event EventHandler? DevicesChanged;
    public event EventHandler<bool>? ScanningChanged;

    public IReadOnlyList<DiscoveredDevice> Devices
    {
        get
        {
            lock (_lock)
                return _all.Values
                    .Where(d => ScanMatcher.Passes(Filter, NameFilter, d.Name, d.AdvertisesService))
                    .OrderByDescending(d => d.Rssi)
                    .ToList();
        }
    }

    public void Start(TimeSpan duration)
    {
        lock (_lock) _all.Clear();
        _stopCts?.Cancel();
        _stopCts = new CancellationTokenSource();
        var token = _stopCts.Token;
        _scanner.Start(serviceFilterInHardware: Filter == ScanFilter.Service);
        ScanningChanged?.Invoke(this, true);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        _ = Task.Delay(duration, token).ContinueWith(t => { if (!t.IsCanceled) Stop(); }, TaskScheduler.Default);
    }

    public void Stop()
    {
        _stopCts?.Cancel();
        if (_scanner.IsScanning) _scanner.Stop();
        ScanningChanged?.Invoke(this, false);
    }

    private void OnAdvertisement(object? sender, Advertisement ad)
    {
        lock (_lock)
        {
            _all.TryGetValue(ad.Address, out var existing);
            var name = !string.IsNullOrWhiteSpace(ad.LocalName) ? ad.LocalName!
                : existing?.Name ?? $"Unnamed ({BluetoothAddress.Format(ad.Address)})";
            _all[ad.Address] = new DiscoveredDevice(ad.Address, name, ad.Rssi,
                ad.AdvertisesService || (existing?.AdvertisesService ?? false), DateTimeOffset.Now);
        }
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}
