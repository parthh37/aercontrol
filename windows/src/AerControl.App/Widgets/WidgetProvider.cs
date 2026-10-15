using System.Runtime.InteropServices;
using System.Text.Json;
using AerControl.Core;
using Microsoft.Windows.Widgets.Providers;

namespace AerControl.App.Widgets;

/// <summary>
/// Windows 11 Widgets Board provider. Windows activates it over COM (registered in
/// Package.appxmanifest and by <see cref="WidgetComServer"/>). It lives in the app
/// process, so it calls the shared SprayController directly; no IPC needed.
/// </summary>
[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid(Clsid)]
public sealed class WidgetProvider : IWidgetProvider
{
    public const string Clsid = "860110C8-B6B9-4D78-A0F2-EDE571EB78C7";
    public const string DefinitionId = "AerSpray";

    // Static: Windows may create several provider instances over the process lifetime.
    private static readonly Dictionary<string, (WidgetSize Size, bool Active)> Widgets = new();
    private static readonly object Lock = new();
    private static bool _subscribed;
    private static bool _busy;

    public WidgetProvider()
    {
        // Recover widgets that existed before this process started.
        foreach (var info in WidgetManager.GetDefault().GetWidgetInfos())
            lock (Lock) Widgets[info.WidgetContext.Id] = (info.WidgetContext.Size, info.WidgetContext.IsActive);
        _ = EnsureSubscribedAsync();
    }

    private static async Task EnsureSubscribedAsync()
    {
        await AppServices.EnsureInitializedAsync();
        lock (Lock)
        {
            if (_subscribed) return;
            _subscribed = true;
        }
        AppServices.Controller.Changed += (_, _) => UpdateAll();
        UpdateAll();
    }

    public void CreateWidget(WidgetContext context)
    {
        lock (Lock) Widgets[context.Id] = (context.Size, true);
        _ = UpdateAsync(context.Id);
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        lock (Lock) Widgets.Remove(widgetId);
    }

    public void Activate(WidgetContext context)
    {
        lock (Lock) Widgets[context.Id] = (context.Size, true);
        _ = UpdateAsync(context.Id);
    }

    public void Deactivate(string widgetId)
    {
        lock (Lock)
            if (Widgets.TryGetValue(widgetId, out var w)) Widgets[widgetId] = (w.Size, false);
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs args)
    {
        var ctx = args.WidgetContext;
        lock (Lock) Widgets[ctx.Id] = (ctx.Size, ctx.IsActive);
        _ = UpdateAsync(ctx.Id);
    }

    public async void OnActionInvoked(WidgetActionInvokedArgs args)
    {
        if (args.Verb != "spray") return;
        await AppServices.EnsureInitializedAsync();
        _busy = true;
        UpdateAll();
        try
        {
            await AppServices.Controller.SprayAsync(SprayTrigger.Widget);
        }
        finally
        {
            _busy = false;
            UpdateAll();
        }
    }

    private static void UpdateAll()
    {
        string[] ids;
        lock (Lock) ids = Widgets.Where(w => w.Value.Active).Select(w => w.Key).ToArray();
        foreach (var id in ids) _ = UpdateAsync(id);
    }

    private static async Task UpdateAsync(string widgetId)
    {
        try
        {
            await AppServices.EnsureInitializedAsync();
            WidgetSize size;
            lock (Lock)
            {
                if (!Widgets.TryGetValue(widgetId, out var w)) return;
                size = w.Size;
            }
            var options = new WidgetUpdateRequestOptions(widgetId)
            {
                Template = size == WidgetSize.Small ? WidgetTemplates.Small : WidgetTemplates.Medium,
                Data = BuildData(),
                CustomState = "",
            };
            WidgetManager.GetDefault().UpdateWidget(options);
        }
        catch (Exception ex)
        {
            AppServices.Log?.Warn($"widget update failed: {ex.Message}");
        }
    }

    /// <summary>Data JSON bound into the Adaptive Card template (${...}).</summary>
    internal static string BuildData()
    {
        var c = AppServices.Controller;
        var status = c.Session.Status;
        var last = c.LastSpray;
        var data = new Dictionary<string, object?>
        {
            ["deviceName"] = c.Settings.DeviceName ?? "AerControl",
            ["status"] = _busy ? "Spraying…" : status.Label(),
            ["statusColor"] = status switch
            {
                LinkStatus.Connected => "Good",
                LinkStatus.Connecting => "Warning",
                LinkStatus.BluetoothOff or LinkStatus.Unsupported or LinkStatus.NoDevice => "Attention",
                _ => "Default",
            },
            ["lastSpray"] = last is null ? "No sprays yet"
                : $"{(last.Success ? "✓" : "⚠")} {Pages.SprayPage.Ago(last.Date)}",
            ["battery"] = c.DeviceStatus?.BatteryText ?? "—",
            ["refill"] = c.DeviceStatus?.RefillText ?? "—",
            ["canSpray"] = !_busy && c.Settings.DeviceAddress is not null,
        };
        return JsonSerializer.Serialize(data);
    }
}

/// <summary>Adaptive Card templates (schema 1.5, the version the Widgets Board renders).</summary>
internal static class WidgetTemplates
{
    public const string Small = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        { "type": "TextBlock", "text": "${deviceName}", "weight": "Bolder", "size": "Medium", "wrap": false },
        { "type": "TextBlock", "text": "● ${status}", "color": "${statusColor}", "size": "Small", "spacing": "None" },
        { "type": "TextBlock", "text": "${lastSpray}", "size": "Small", "isSubtle": true, "spacing": "Small" }
      ],
      "actions": [
        { "type": "Action.Execute", "title": "Spray", "verb": "spray", "style": "positive", "isEnabled": "${canSpray}" }
      ]
    }
    """;

    public const string Medium = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        {
          "type": "ColumnSet",
          "columns": [
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                { "type": "TextBlock", "text": "${deviceName}", "weight": "Bolder", "size": "Large", "wrap": false },
                { "type": "TextBlock", "text": "● ${status}", "color": "${statusColor}", "spacing": "None" },
                { "type": "TextBlock", "text": "Last spray: ${lastSpray}", "size": "Small", "isSubtle": true, "wrap": true }
              ]
            },
            {
              "type": "Column",
              "width": "auto",
              "items": [
                { "type": "TextBlock", "text": "🔋 ${battery}", "horizontalAlignment": "Right" },
                { "type": "TextBlock", "text": "💧 ${refill}", "horizontalAlignment": "Right", "spacing": "Small" }
              ]
            }
          ]
        }
      ],
      "actions": [
        { "type": "Action.Execute", "title": "Spray now", "verb": "spray", "style": "positive", "isEnabled": "${canSpray}" }
      ]
    }
    """;
}

/// <summary>
/// Registers the provider's class factory with COM for this process. It's called from Main
/// on every launch, so Widgets Board activations reach the already-running instance
/// (REGCLS_MULTIPLEUSE) instead of starting a second process.
/// </summary>
internal static class WidgetComServer
{
    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private static uint _cookie;

    public static void Register()
    {
        try
        {
            int hr = CoRegisterClassObject(Guid.Parse(WidgetProvider.Clsid), new WidgetProviderFactory(),
                                           CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out _cookie);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        catch (Exception ex)
        {
            // Unpackaged runs have no widget registration; the app works without it.
            System.Diagnostics.Debug.WriteLine($"widget COM registration failed: {ex.Message}");
        }
    }

    public static void Revoke()
    {
        if (_cookie != 0) CoRevokeClassObject(_cookie);
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);
}

[ComImport]
[ComVisible(false)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}

[ComVisible(true)]
internal sealed class WidgetProviderFactory : IClassFactory
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
        if (riid != typeof(WidgetProvider).GUID && riid != IID_IUnknown) return E_NOINTERFACE;
        ppvObject = WinRT.MarshalInspectable<IWidgetProvider>.FromManaged(new WidgetProvider());
        return 0;
    }

    public int LockServer(bool fLock) => 0;
}
