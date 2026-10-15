using AerControl.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AerControl.App.Pages;

public sealed class HistoryItem(SprayRecord r)
{
    public string Glyph { get; } = r.Success ? "\uE73E" : "\uE711"; // CheckMark / Cancel
    public Brush Brush { get; } = new SolidColorBrush(r.Success ? Colors.SeaGreen : Colors.IndianRed);
    public string Time { get; } = r.Date.LocalDateTime.ToString("g");
    public string Source { get; } = r.Trigger.ToString();
    public string MessageNumber { get; } = r.MessageNumber?.ToString() ?? "–";
    public string Details { get; } = r.Error ?? r.WriteMode ?? "";
}

public sealed partial class SprayPage : Page
{
    private SprayRecord? _flash;
    private DispatcherTimer? _flashTimer;

    public SprayPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppServices.Controller.Changed += OnChanged;
            Updates.Changed += OnChanged;
            AppServices.Controller.RefreshDeviceStatusIfOlder(TimeSpan.FromMinutes(10));
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppServices.Controller.Changed -= OnChanged;
            Updates.Changed -= OnChanged;
        };
    }

    private void OnChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    public static Brush StatusBrush(LinkStatus s) => new SolidColorBrush(s switch
    {
        LinkStatus.Connected => Colors.SeaGreen,
        LinkStatus.Connecting => Colors.Orange,
        LinkStatus.NoDevice => Colors.Goldenrod,
        LinkStatus.BluetoothOff or LinkStatus.Unsupported => Colors.IndianRed,
        _ => Colors.Gray,
    });

    private void Refresh()
    {
        var c = AppServices.Controller;
        var settings = c.Settings;
        bool hasDevice = settings.DeviceAddress is not null;
        var status = c.Session.Status;

        DeviceNameText.Text = settings.DeviceName ?? (hasDevice ? BluetoothAddress.Format(settings.DeviceAddress!.Value) : "No device selected");
        StatusText.Text = status.Label();
        StatusDot.Fill = StatusBrush(status);
        BatteryText.Text = c.DeviceStatus?.BatteryText ?? "—";
        RefillText.Text = c.DeviceStatus?.RefillText ?? "—";
        RefreshStatusButton.IsEnabled = hasDevice;
        RefillHint.Visibility = c.DeviceStatus?.RefillNeedsReset == true ? Visibility.Visible : Visibility.Collapsed;
        ResetRefillButton.IsEnabled = hasDevice;

        var last = c.LastSpray;
        LastSprayText.Text = last is null ? "No sprays yet"
            : $"Last spray {(last.Success ? "succeeded" : "failed")} {Ago(last.Date)}";

        SprayButton.IsEnabled = hasDevice && !c.IsSpraying;
        SprayProgress.IsActive = c.IsSpraying;
        SprayLabel.Visibility = c.IsSpraying ? Visibility.Collapsed : Visibility.Visible;
        SprayGlyph.Glyph = _flash is null ? "\uE945" : _flash.Success ? "\uE73E" : "\uE711";

        ConnectButton.Visibility = hasDevice && status != LinkStatus.Connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = status != LinkStatus.Connecting;
        DisconnectButton.Visibility = status == LinkStatus.Connected ? Visibility.Visible : Visibility.Collapsed;

        HistoryList.ItemsSource = c.History.Select(r => new HistoryItem(r)).ToList();

        if (Updates.Available is { } update)
        {
            UpdateBar.Message = $"AerControl {update.Version} is available (you have {Updates.CurrentVersion}).";
            UpdateBar.IsOpen = true;
        }
    }

    public static string Ago(DateTimeOffset date)
    {
        var d = DateTimeOffset.Now - date;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return date.LocalDateTime.ToString("g");
    }

    private async void OnSprayClick(object sender, RoutedEventArgs e)
    {
        var record = await AppServices.Controller.SprayAsync(SprayTrigger.Button);
        _flash = record;
        ErrorText.Text = record.Error ?? "";
        ErrorText.Visibility = record.Success ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
        _flashTimer?.Stop();
        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flash = null;
            Refresh();
        };
        _flashTimer.Start();
    }

    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await AppServices.Controller.Session.ConnectAsync();
            ErrorText.Visibility = Visibility.Collapsed;
            // The window stays open (unlike the macOS menu, which re-reads on every open),
            // so read battery/refill now that the link is up.
            AppServices.Controller.RefreshDeviceStatusIfOlder(TimeSpan.FromMinutes(10));
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e) => await Updates.OpenDownloadAsync();

    private async void OnRefreshStatusClick(object sender, RoutedEventArgs e)
    {
        RefreshStatusButton.IsEnabled = false;
        var status = await AppServices.Controller.RefreshDeviceStatusAsync();
        ErrorText.Text = status is null ? "Couldn't read the device status (see log)." : "";
        ErrorText.Visibility = status is null ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    private async void OnResetRefillClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reset the refill level?",
            Content = "This tells the aer that a fresh can is in, which sets its spray counter back to zero (like “Reset level” in the Godrej app).",
            PrimaryButtonText = "I inserted a new refill: reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await AppServices.Controller.ResetRefillAsync();
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        Refresh();
    }

    private async void OnDisconnectClick(object sender, RoutedEventArgs e) =>
        await AppServices.Controller.Session.DisconnectAsync("user");

    private void OnClearHistoryClick(object sender, RoutedEventArgs e) => AppServices.Controller.ClearHistory();
}
