using AerControl.Core;
using AerControl.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AerControl.App.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly WriteMode[] WriteModes = [WriteMode.Automatic, WriteMode.WithResponse, WriteMode.WithoutResponse];
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        _loading = true;
        var s = AppServices.Controller.Settings;
        IdleBox.Value = s.IdleDisconnectSeconds;
        AutoReconnectSwitch.IsOn = s.AutoReconnect;
        WriteModeBox.SelectedIndex = Array.IndexOf(WriteModes, s.WriteMode);
        AutoIncrementSwitch.IsOn = s.AutoIncrementMessageNumber;
        MessageNumberBox.Value = s.MessageNumber;
        UpdatePayloadPreview(s.MessageNumber);
        VersionText.Text = $"Version {typeof(SettingsPage).Assembly.GetName().Version}";
        LoadExtraSettings();
        _loading = false;
    }

    /// <summary>Phase 3 adds schedule, hotkey and launch-at-login controls.</summary>
    partial void LoadExtraSettings();

    private void UpdatePayloadPreview(uint mn) =>
        PayloadText.Text = Hex.ToHex(new SprayPayload(MessageNumber: mn).Encode());

    private Task Update(Action<AerSettings> change) =>
        _loading ? Task.CompletedTask : AppServices.Controller.UpdateSettingsAsync(change);

    private async void OnIdleChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;
        await Update(s => s.IdleDisconnectSeconds = (int)args.NewValue);
    }

    private async void OnAutoReconnectToggled(object sender, RoutedEventArgs e) =>
        await Update(s => s.AutoReconnect = AutoReconnectSwitch.IsOn);

    private async void OnWriteModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WriteModeBox.SelectedIndex < 0) return;
        var mode = WriteModes[WriteModeBox.SelectedIndex];
        await Update(s => s.WriteMode = mode);
    }

    private async void OnAutoIncrementToggled(object sender, RoutedEventArgs e) =>
        await Update(s => s.AutoIncrementMessageNumber = AutoIncrementSwitch.IsOn);

    private async void OnMessageNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;
        var mn = (uint)Math.Clamp(args.NewValue, 0, uint.MaxValue);
        UpdatePayloadPreview(mn);
        await Update(s => s.MessageNumber = mn);
    }

    private async void OnOpenLogsClick(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(AppServices.Log.FilePath)!);
}
