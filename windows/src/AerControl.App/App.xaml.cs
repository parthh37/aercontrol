using AerControl.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AerControl.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public static DispatcherQueue UI { get; private set; } = null!;
    public static TrayIcon? Tray { get; private set; }
    public static GlobalHotKey? HotKey { get; private set; }
    private static SprayScheduler? _scheduler;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppServices.Log?.Error($"unhandled: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        UI = DispatcherQueue.GetForCurrentThread();
        await AppServices.EnsureInitializedAsync();

        Window = new MainWindow { HideOnClose = true };
        Tray = new TrayIcon();
        _scheduler = new SprayScheduler(UI);
        SprayScheduler.RefreshStatusIfStale();
        Updates.CheckIfDue();
        ApplyHotKey();
        _ = JumpListActions.InstallAsync();

        var launchArgs = (args.Arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool handled = HandleCommandLine(launchArgs.Concat(Environment.GetCommandLineArgs()));
        if (!handled && !StartsInBackground(AppInstance.GetCurrent().GetActivatedEventArgs()))
            Window.Activate();
    }

    /// <summary>Registers or releases the global hotkey to match settings.</summary>
    public static void ApplyHotKey()
    {
        bool wanted = AppServices.Controller.Settings.HotkeyEnabled;
        if (wanted && HotKey is null && Window is not null)
        {
            HotKey = new GlobalHotKey(Window, () => _ = AppServices.Controller.SprayAsync(SprayTrigger.Hotkey));
            AppServices.Log.Info(HotKey.IsRegistered ? $"hotkey {GlobalHotKey.Display} registered" : "hotkey registration failed");
        }
        else if (!wanted && HotKey is not null)
        {
            HotKey.Dispose();
            HotKey = null;
        }
    }

    public static void Quit()
    {
        HotKey?.Dispose();
        Tray?.Dispose();
        _ = AppServices.Controller.Session.DisconnectAsync("quit");
        if (Window is not null) Window.HideOnClose = false;
        Current.Exit();
    }

    /// <summary>
    /// Stay hidden when Windows launched us for the widget board (COM server) or at login.
    /// </summary>
    private static bool StartsInBackground(AppActivationArguments activation) =>
        Environment.GetCommandLineArgs().Any(a => a.Equals("-RegisterProcessAsComServer", StringComparison.OrdinalIgnoreCase))
        || activation.Kind == ExtendedActivationKind.StartupTask;

    /// <summary>A second launch was forwarded here (single instance). Runs on a background thread.</summary>
    public static void OnRedirectedActivation(AppActivationArguments args)
    {
        string[] commandLine = args.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        UI?.TryEnqueue(() =>
        {
            if (!HandleCommandLine(commandLine)) Window?.ShowAndFocus();
        });
    }

    /// <returns>true if the arguments asked for an action (so the window needn't be shown).</returns>
    public static bool HandleCommandLine(IEnumerable<string> args)
    {
        if (args.Any(a => a.Equals("--spray", StringComparison.OrdinalIgnoreCase)))
        {
            _ = AppServices.Controller.SprayAsync(SprayTrigger.Tray);
            return true;
        }
        return false;
    }
}
