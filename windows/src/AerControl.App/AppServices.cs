using AerControl.Ble.Windows;
using AerControl.Core;

namespace AerControl.App;

/// <summary>
/// Process-wide services. The window, tray, scheduler, hotkey and widget provider share
/// one <see cref="SprayController"/>, and so one BLE link.
/// </summary>
public static class AppServices
{
    private static readonly Lazy<Task> _init = new(InitializeCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    public static FileLogger Log { get; private set; } = null!;
    public static SprayController Controller { get; private set; } = null!;
    public static ScanSession Scan { get; private set; } = null!;

    /// <summary>Idempotent. The widget provider may call this before the UI exists.</summary>
    public static Task EnsureInitializedAsync() => _init.Value;

    private static async Task InitializeCoreAsync()
    {
        try
        {
            // Packaged: keep data in the package's LocalFolder (removed on uninstall).
            AppPaths.DataDirectory = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch (Exception)
        {
            // Unpackaged debug run: stay with %LOCALAPPDATA%\AerControl.
        }

        Log = FileLogger.Shared;
        Log.Info($"AerControl {typeof(AppServices).Assembly.GetName().Version} starting");
        var (session, scan) = WinRtBle.Create(Log);
        Controller = new SprayController(session, Log, AppPaths.SettingsFile, AppPaths.HistoryFile);
        Scan = scan;
        var s = Controller.Settings;
        Scan.Filter = s.ScanFilter;
        Scan.NameFilter = s.NameFilter;
        await Controller.InitializeAsync().ConfigureAwait(false);
    }
}
