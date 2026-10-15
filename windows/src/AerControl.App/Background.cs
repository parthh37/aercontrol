using System.Runtime.InteropServices;
using System.Windows.Input;
using AerControl.Core;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel;
using Windows.UI.StartScreen;

namespace AerControl.App;

/// <summary>Minimal ICommand for tray menu items (PopupMenu mode invokes Commands, not Click).</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

/// <summary>System tray icon with a native context menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutItem _statusItem = new() { IsEnabled = false };

    public TrayIcon()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Spray now", Command = new RelayCommand(() => _ = AppServices.Controller.SprayAsync(SprayTrigger.Tray)) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Open AerControl", Command = new RelayCommand(() => App.Window?.ShowAndFocus()) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Disconnect", Command = new RelayCommand(() => _ = AppServices.Controller.Session.DisconnectAsync("tray")) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit", Command = new RelayCommand(App.Quit) });

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AerControl.ico")),
            ToolTipText = "AerControl",
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            NoLeftClickDelay = true,
            // Left click sprays: the tray icon is the quickest "button" on Windows.
            LeftClickCommand = new RelayCommand(() => _ = AppServices.Controller.SprayAsync(SprayTrigger.Tray)),
            DoubleClickCommand = new RelayCommand(() => App.Window?.ShowAndFocus()),
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);

        AppServices.Controller.Changed += OnChanged;
        Update();
    }

    private void OnChanged(object? sender, EventArgs e) => App.UI.TryEnqueue(Update);

    private void Update()
    {
        var c = AppServices.Controller;
        var name = c.Settings.DeviceName ?? "No device";
        var last = c.LastSpray is { } l ? $"last spray {Pages.SprayPage.Ago(l.Date)}{(l.Success ? "" : " (failed)")}" : "no sprays yet";
        _statusItem.Text = $"{name}: {c.Session.Status.Label()}";
        _icon.ToolTipText = $"AerControl: {name}, {c.Session.Status.Label()}\n{last}";
    }

    public void ShowNotification(string title, string message) =>
        _icon.ShowNotification(title, message, NotificationIcon.Warning);

    public void Dispose()
    {
        AppServices.Controller.Changed -= OnChanged;
        _icon.Dispose();
    }
}

/// <summary>Sprays every N minutes inside the configured daily window.</summary>
public sealed class SprayScheduler
{
    private readonly DispatcherQueueTimer _timer;

    public SprayScheduler(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>From history, so the cadence survives restarts.</summary>
    private static DateTime? LastScheduledSpray =>
        AppServices.Controller.History.FirstOrDefault(r => r.Trigger == SprayTrigger.Schedule)?.Date.LocalDateTime;

    private static void Tick()
    {
        var c = AppServices.Controller;
        if (c.IsSpraying) return;
        if (new ScheduleEvaluator(c.Settings.Schedule).IsDue(DateTime.Now, LastScheduledSpray))
        {
            c.Log.Info("scheduled spray due");
            _ = c.SprayAsync(SprayTrigger.Schedule);
        }
    }

    public static DateTime? NextDue()
    {
        var schedule = AppServices.Controller.Settings.Schedule;
        if (!schedule.Enabled || schedule.IntervalMinutes <= 0) return null;
        var e = new ScheduleEvaluator(schedule);
        var last = LastScheduledSpray;
        var t = DateTime.Now;
        for (int i = 0; i < 48 * 60; i++, t = t.AddMinutes(1))
            if (e.IsDue(t, last)) return t;
        return null;
    }
}

/// <summary>
/// Global hotkey (Ctrl+Alt+Shift+S) via RegisterHotKey on the main window, with the window
/// procedure subclassed to receive WM_HOTKEY. The window is hidden, never destroyed, while
/// the tray is alive, so the registration persists.
/// </summary>
public sealed class GlobalHotKey : IDisposable
{
    public const string Display = "Ctrl+Alt+Shift+S";
    private const int HotKeyId = 0xAE01;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    private const uint VK_S = 0x53;
    private const uint WM_HOTKEY = 0x0312;

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc; // keep the delegate alive
    private readonly Action _action;
    public bool IsRegistered { get; }

    public GlobalHotKey(Window window, Action action)
    {
        _action = action;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _proc = WndProc;
        SetWindowSubclass(_hwnd, _proc, (UIntPtr)HotKeyId, UIntPtr.Zero);
        IsRegistered = RegisterHotKey(_hwnd, HotKeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_S);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId) _action();
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, HotKeyId);
        RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)HotKeyId);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Launch at login through the MSIX StartupTask declared in Package.appxmanifest.</summary>
public static class LoginItem
{
    public const string TaskId = "AerControlStartup";

    public static async Task<bool> IsEnabledAsync()
    {
        try { return (await StartupTask.GetAsync(TaskId)).State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy; }
        catch (Exception) { return false; } // unpackaged run
    }

    /// <returns>The resulting state, which may be DisabledByUser (Task Manager / Settings wins).</returns>
    public static async Task<StartupTaskState?> SetEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled) return await task.RequestEnableAsync();
            task.Disable();
            return task.State;
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn($"startup task: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Jump list tasks: right-click the taskbar or Start entry, then "Spray now".</summary>
public static class JumpListActions
{
    public static async Task InstallAsync()
    {
        try
        {
            if (!JumpList.IsSupported()) return;
            var list = await JumpList.LoadCurrentAsync();
            list.Items.Clear();
            var spray = JumpListItem.CreateWithArguments("--spray", "Spray now");
            spray.Description = "Trigger the aer Smart Matic once";
            spray.Logo = new Uri("ms-appx:///Assets/Square44x44Logo.png");
            list.Items.Add(spray);
            list.SystemGroupKind = JumpListSystemGroupKind.None;
            await list.SaveAsync();
        }
        catch (Exception ex)
        {
            AppServices.Log.Warn($"jump list: {ex.Message}");
        }
    }
}
