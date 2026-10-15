using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AerControl.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): enforces a single instance, so a second
/// launch (Start menu, jump list "Spray now", widget COM activation) is forwarded to the
/// running process, which owns the one BLE connection the aer allows.
/// </summary>
public static class Program
{
    public const string InstanceKey = "AerControl.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToExistingInstance())
            return 0;

        // Before the UI starts, so a Widgets Board activation is served by this process.
        Widgets.WidgetComServer.Register();

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        Widgets.WidgetComServer.Revoke();
        return 0;
    }

    /// <returns>true when another instance owns the key and has been handed our activation.</returns>
    private static bool RedirectToExistingInstance()
    {
        var current = AppInstance.GetCurrent();
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        // RedirectActivationToAsync needs a pumping wait on an STA thread, otherwise it can
        // deadlock. This is the pattern from the Windows App SDK single-instancing sample.
        var args = current.GetActivatedEventArgs();
        var redirectDone = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectDone);
        });
        _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [redirectDone], out _);
        CloseHandle(redirectDone);
        return true;
    }

    private const uint CWMO_DEFAULT = 0;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, ulong nHandles,
                                                        IntPtr[] pHandles, out uint dwIndex);
}
