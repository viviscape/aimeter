using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AIMeter.UI;

/// <summary>Full usage breakdown for one account, opened by clicking its tile in the widget.</summary>
public partial class DetailWindow : Window
{
    private readonly App _app;
    private readonly AccountViewModel _vm;

    public DetailWindow(App app, AccountViewModel vm)
    {
        _app = app;
        _vm = vm;
        InitializeComponent();
        Icon = app.WindowIcon;
        DataContext = vm;
        UpdateTitle();
        vm.PropertyChanged += Vm_PropertyChanged;
        Closed += (_, _) => vm.PropertyChanged -= Vm_PropertyChanged;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(AccountViewModel.Label)) UpdateTitle();
    }

    private void UpdateTitle() => Title = $"{_vm.Label} — AI Meter";

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _app.RefreshAndUpdateTrayAsync(_vm);
    private async void SignInAgain_Click(object sender, RoutedEventArgs e) => await _app.ReauthenticateAsync(_vm);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Match the widget's dark look; ignored on Windows versions without dark title bars.
    private void UseDarkTitleBar()
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        var on = 1;
        try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)); }
        catch { }
    }
}
