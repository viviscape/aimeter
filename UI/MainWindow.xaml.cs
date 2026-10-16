using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AIMeter.UI;

public partial class MainWindow : Window
{
    private readonly App _app;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        AccountsList.ItemsSource = app.Accounts;
        app.Accounts.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();
        Loaded += (_, _) => PlaceWindow();
    }

    // Alt+F4, or the installer's close request during upgrade/uninstall, quits the whole app
    // instead of leaving an invisible tray process holding the exe open.
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _app.ExitApp();
    }

    private void UpdateEmptyState()
        => EmptyPanel.Visibility = _app.Accounts.Any() ? Visibility.Collapsed : Visibility.Visible;

    public void SetUpdated(DateTimeOffset? at, bool refreshing)
    {
        UpdatedText.Text = refreshing ? "updating…"
            : at is { } t ? "updated " + t.ToLocalTime().ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"))
            : "";
    }

    public void ApplyTopmost(bool topmost)
    {
        Topmost = topmost;
        TopmostMenu.IsChecked = topmost;
    }

    private void PlaceWindow()
    {
        var s = _app.Store.Settings;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (s.Left is { } l && s.Top is { } t && vs.Contains(new Point(l + 40, t + 20)))
        {
            Left = l;
            Top = t;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 12;
            Top = wa.Top + 12;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        _app.Store.Settings.Left = Left;
        _app.Store.Settings.Top = Top;
        _app.SaveStore();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _app.RefreshAllAsync();
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowAccountsWindow();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Exit_Click(object sender, RoutedEventArgs e) => _app.ExitApp();

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _app.Store.Settings.AlwaysOnTop = TopmostMenu.IsChecked;
        _app.ApplySettings();
    }

    private async void SignInAgain_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is AccountViewModel vm)
            await _app.ReauthenticateAsync(vm);
    }
}
