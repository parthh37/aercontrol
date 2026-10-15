using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AerControl.App.Pages;

/// <summary>Phase 3 settings (schedule, hotkey, launch at login), built in code.</summary>
public sealed partial class SettingsPage
{
    private ToggleSwitch? _scheduleSwitch, _hotkeySwitch, _loginSwitch;
    private NumberBox? _intervalBox;
    private TimePicker? _startPicker, _endPicker;
    private TextBlock? _nextText, _hotkeyError, _loginNote;

    partial void LoadExtraSettings()
    {
        if (_scheduleSwitch is null) BuildExtraSettings();
        var s = AppServices.Controller.Settings;
        _scheduleSwitch!.IsOn = s.Schedule.Enabled;
        _intervalBox!.Value = s.Schedule.IntervalMinutes;
        _startPicker!.Time = TimeSpan.FromMinutes(s.Schedule.Window.StartMinutes);
        _endPicker!.Time = TimeSpan.FromMinutes(s.Schedule.Window.EndMinutes);
        _hotkeySwitch!.IsOn = s.HotkeyEnabled;
        _hotkeyError!.Visibility = s.HotkeyEnabled && App.HotKey is { IsRegistered: false } ? Visibility.Visible : Visibility.Collapsed;
        UpdateNext();
        _ = LoadLoginStateAsync();
    }

    private async Task LoadLoginStateAsync()
    {
        var on = await LoginItem.IsEnabledAsync();
        _loading = true;
        _loginSwitch!.IsOn = on;
        _loading = false;
    }

    private void UpdateNext()
    {
        var next = SprayScheduler.NextDue();
        _nextText!.Text = next is DateTime t ? $"Next scheduled spray: {t:g}" : "";
    }

    private void BuildExtraSettings()
    {
        TextBlock Heading(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 16, 0, 0) };
        TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };

        _scheduleSwitch = new ToggleSwitch { Header = "Spray on a schedule" };
        _intervalBox = new NumberBox { Header = "Every (minutes)", Minimum = 1, Maximum = 720, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        _startPicker = new TimePicker { Header = "Between" };
        _endPicker = new TimePicker { Header = "and" };
        _nextText = Note("");
        _hotkeySwitch = new ToggleSwitch { Header = $"Global hotkey {GlobalHotKey.Display} sprays" };
        _hotkeyError = Note($"{GlobalHotKey.Display} is already used by another app.");
        _loginSwitch = new ToggleSwitch { Header = "Launch at login (starts in the tray)" };
        _loginNote = Note("");

        _scheduleSwitch.Toggled += async (_, _) => { await Update(s => s.Schedule.Enabled = _scheduleSwitch.IsOn); UpdateNext(); };
        _intervalBox.ValueChanged += async (_, a) =>
        {
            if (double.IsNaN(a.NewValue)) return;
            await Update(s => s.Schedule.IntervalMinutes = (int)a.NewValue);
            UpdateNext();
        };
        _startPicker.TimeChanged += async (_, a) =>
        {
            await Update(s => s.Schedule.Window = s.Schedule.Window with { StartMinutes = (int)a.NewTime.TotalMinutes });
            UpdateNext();
        };
        _endPicker.TimeChanged += async (_, a) =>
        {
            await Update(s => s.Schedule.Window = s.Schedule.Window with { EndMinutes = (int)a.NewTime.TotalMinutes });
            UpdateNext();
        };
        _hotkeySwitch.Toggled += async (_, _) =>
        {
            await Update(s => s.HotkeyEnabled = _hotkeySwitch.IsOn);
            App.ApplyHotKey();
            _hotkeyError.Visibility = _hotkeySwitch.IsOn && App.HotKey is { IsRegistered: false } ? Visibility.Visible : Visibility.Collapsed;
        };
        _loginSwitch.Toggled += async (_, _) =>
        {
            if (_loading) return;
            var state = await LoginItem.SetEnabledAsync(_loginSwitch.IsOn);
            _loginNote.Text = state == Windows.ApplicationModel.StartupTaskState.DisabledByUser
                ? "Startup was disabled in Task Manager › Startup apps; re-enable it there."
                : "";
        };

        foreach (var e in new UIElement[]
                 {
                     Heading("Schedule"), _scheduleSwitch, _intervalBox, _startPicker, _endPicker, _nextText,
                     Heading("Shortcuts"), _hotkeySwitch, _hotkeyError,
                     Note("Right-click AerControl in the taskbar or Start menu for “Spray now”. Left-click the tray icon to spray."),
                     Heading("General"), _loginSwitch, _loginNote,
                 })
            ExtraSettings.Children.Add(e);
    }
}
