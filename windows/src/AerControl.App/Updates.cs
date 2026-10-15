using System.Runtime.InteropServices;
using AerControl.Core;

namespace AerControl.App;

/// <summary>Daily GitHub release check: tray notification, Spray page banner, Settings status.</summary>
public static class Updates
{
    public static AppRelease? Available { get; private set; }
    public static string? State { get; private set; }
    public static event EventHandler? Changed;

    public static string CurrentVersion
    {
        get
        {
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch (Exception)
            {
                var v = typeof(Updates).Assembly.GetName().Version;
                return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }
    }

    public static string Architecture => RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "ARM64" : "x64";

    public static void CheckIfDue()
    {
        var s = AppServices.Controller.Settings;
        if (!s.CheckForUpdates) return;
        if (s.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(24)) return;
        _ = CheckAsync(notify: true);
    }

    public static async Task CheckAsync(bool notify)
    {
        try
        {
            var release = await UpdateChecker.LatestReleaseAsync();
            await AppServices.Controller.UpdateSettingsAsync(s => s.LastUpdateCheck = DateTimeOffset.Now);
            if (UpdateChecker.IsNewer(release.Version, CurrentVersion))
            {
                bool isNew = Available?.Version != release.Version;
                Available = release;
                State = $"Version {release.Version} is available.";
                AppServices.Log.Info($"update available: {release.Version} (running {CurrentVersion})");
                if (notify && isNew)
                    App.UI.TryEnqueue(() => App.Tray?.ShowNotification("AerControl update available",
                        $"Version {release.Version} is ready to download."));
            }
            else
            {
                Available = null;
                State = $"You're up to date ({CurrentVersion}).";
            }
        }
        catch (Exception ex)
        {
            State = $"Couldn't check for updates: {ex.Message}";
            AppServices.Log.Warn($"update check failed: {ex.Message}");
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static async Task OpenDownloadAsync()
    {
        if (Available is { } r) await Windows.System.Launcher.LaunchUriAsync(r.WindowsDownloadUrl(Architecture));
    }
}
