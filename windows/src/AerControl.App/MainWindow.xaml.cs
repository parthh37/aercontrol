using AerControl.App.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AerControl.App;

public sealed partial class MainWindow : Window
{
    /// <summary>When true, closing the window hides it (the tray keeps the app alive).</summary>
    public bool HideOnClose { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AerControl.ico");
        AppWindow.Resize(new SizeInt32(820, 640));

        AppWindow.Closing += (_, e) =>
        {
            if (!HideOnClose) return;
            e.Cancel = true;
            AppWindow.Hide();
        };

        var start = AppServices.Controller.Settings.DeviceAddress is null ? 1 : 0;
        Nav.SelectedItem = Nav.MenuItems[start];
    }

    public void ShowAndFocus()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
        Activate();
    }

    public void Navigate(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag as string switch
        {
            "devices" => typeof(DevicesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(SprayPage),
        };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }
}
