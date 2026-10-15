using AerControl.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AerControl.App.Pages;

public sealed class DeviceItem(DiscoveredDevice d, bool selected)
{
    public ulong Address { get; } = d.Address;
    public string Name { get; } = d.Name;
    public string RssiText { get; } = $"{d.Rssi} dBm";
    public string Subtitle { get; } = d.AddressText + (d.AdvertisesService ? " · aer service" : "");
    public string ActionText { get; } = selected ? "Selected" : "Use";
    public bool CanSelect { get; } = !selected;
    public DiscoveredDevice Device { get; } = d;
}

public sealed partial class DevicesPage : Page
{
    private static readonly (ScanFilter Value, string Label)[] Filters =
    [
        (ScanFilter.ServiceOrName, "Service UUID or name"),
        (ScanFilter.Service, "Service UUID only"),
        (ScanFilter.Name, "Name only"),
        (ScanFilter.All, "Show all devices"),
    ];

    private bool _loading;

    public DevicesPage()
    {
        InitializeComponent();
        foreach (var f in Filters) FilterBox.Items.Add(f.Label);
        Loaded += (_, _) =>
        {
            _loading = true;
            var s = AppServices.Controller.Settings;
            FilterBox.SelectedIndex = Array.FindIndex(Filters, f => f.Value == s.ScanFilter);
            NameFilterBox.Text = s.NameFilter;
            _loading = false;
            AppServices.Scan.DevicesChanged += OnScanChanged;
            AppServices.Scan.ScanningChanged += OnScanningChanged;
            AppServices.Controller.Changed += OnScanChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Scan.DevicesChanged -= OnScanChanged;
            AppServices.Scan.ScanningChanged -= OnScanningChanged;
            AppServices.Controller.Changed -= OnScanChanged;
        };
    }

    private void OnScanChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);
    private void OnScanningChanged(object? sender, bool scanning) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        var settings = AppServices.Controller.Settings;
        var devices = AppServices.Scan.Devices;
        DeviceList.ItemsSource = devices.Select(d => new DeviceItem(d, d.Address == settings.DeviceAddress)).ToList();
        EmptyText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        bool scanning = AppServices.Scan.IsScanning;
        ScanProgress.IsActive = scanning;
        ScanButton.Content = scanning ? "Stop" : "Scan";

        SelectedPanel.Visibility = settings.DeviceAddress is null ? Visibility.Collapsed : Visibility.Visible;
        if (settings.DeviceAddress is ulong addr)
        {
            SelectedName.Text = $"Selected: {settings.DeviceName ?? "aer"}";
            SelectedAddress.Text = BluetoothAddress.Format(addr);
        }
    }

    private void OnScanClick(object sender, RoutedEventArgs e)
    {
        if (AppServices.Scan.IsScanning) AppServices.Scan.Stop();
        else AppServices.Scan.Start(TimeSpan.FromSeconds(15));
        Refresh();
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FilterBox.SelectedIndex < 0) return;
        var value = Filters[FilterBox.SelectedIndex].Value;
        AppServices.Scan.Filter = value;
        await AppServices.Controller.UpdateSettingsAsync(s => s.ScanFilter = value);
    }

    private async void OnNameFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var text = NameFilterBox.Text;
        AppServices.Scan.NameFilter = text;
        await AppServices.Controller.UpdateSettingsAsync(s => s.NameFilter = text);
        Refresh();
    }

    private async void OnUseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ulong address }) return;
        var device = AppServices.Scan.Devices.FirstOrDefault(d => d.Address == address);
        if (device is null) return;
        await AppServices.Controller.SelectDeviceAsync(device);
        // Any cached reading belongs to the previous device; read the new one right away.
        AppServices.Controller.RefreshDeviceStatusIfOlder(TimeSpan.Zero);
    }

    private async void OnForgetClick(object sender, RoutedEventArgs e) => await AppServices.Controller.ForgetDeviceAsync();
}
