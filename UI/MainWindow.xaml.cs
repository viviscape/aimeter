using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

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

    // Tiles: a click opens the detail view; pressing and dragging moves the tile.

    private Point? _pressAt;
    private AccountViewModel? _pressed;

    private void Tile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Leave buttons inside the tile ("Sign in again") alone.
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender)) return;
        _pressed = (sender as FrameworkElement)?.DataContext as AccountViewModel;
        _pressAt = e.GetPosition(this);
    }

    private void Tile_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } vm || _pressAt is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _pressed = null;
        _pressAt = null;
        vm.IsDragging = true;
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(AccountViewModel), vm), DragDropEffects.Move); }
        finally
        {
            vm.IsDragging = false;
            _app.SaveAccountOrder();
        }
    }

    private void Tile_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var vm = _pressed;
        _pressed = null;
        _pressAt = null;
        if (vm is not null && ReferenceEquals((sender as FrameworkElement)?.DataContext, vm))
            _app.ShowDetails(vm);
    }

    // Tiles shuffle live while dragging, so dropping just ends the drag.
    private void Tile_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(AccountViewModel)) is not AccountViewModel dragged ||
            (sender as FrameworkElement)?.DataContext is not AccountViewModel target)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        int from = _app.Accounts.IndexOf(dragged), to = _app.Accounts.IndexOf(target);
        if (from == to) return;
        // Only swap once the pointer crosses the target's middle, so tiles of different
        // heights don't flip back and forth under a stationary pointer.
        var tile = (FrameworkElement)sender;
        var lowerHalf = e.GetPosition(tile).Y > tile.ActualHeight / 2;
        if (from < to == lowerHalf) _app.MoveAccount(dragged, to);
    }

    private void Tile_Drop(object sender, DragEventArgs e) => e.Handled = true;

    private static bool IsInsideButton(DependencyObject? node, object tile)
    {
        for (; node is not null && !ReferenceEquals(node, tile);
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase) return true;
        return false;
    }

    private static AccountViewModel? TagOf(object sender) => (sender as FrameworkElement)?.Tag as AccountViewModel;

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is { } vm) _app.ShowDetails(vm);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(TagOf(sender), -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(TagOf(sender), +1);

    private void Move(AccountViewModel? vm, int by)
    {
        if (vm is null) return;
        _app.MoveAccount(vm, _app.Accounts.IndexOf(vm) + by);
        _app.SaveAccountOrder();
    }
}
