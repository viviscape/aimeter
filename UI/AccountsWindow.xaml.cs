using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using AIMeter.Core;

namespace AIMeter.UI;

public partial class AccountsWindow : Window
{
    private static readonly int[] RefreshChoices = { 1, 2, 5, 10, 15, 30, 60 };
    private readonly App _app;
    private CancellationTokenSource? _signInCts;
    private bool _loading = true;

    public AccountsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Icon = app.WindowIcon;
        AccountsList.ItemsSource = app.Accounts;
        app.Accounts.CollectionChanged += Accounts_CollectionChanged;
        UpdateEmpty();

        foreach (var m in RefreshChoices)
            RefreshCombo.Items.Add(new ComboBoxItem { Content = m == 1 ? "1 minute" : m == 60 ? "1 hour" : $"{m} minutes", Tag = m });
        var current = app.Store.Settings.RefreshMinutes;
        RefreshCombo.SelectedItem = RefreshCombo.Items.Cast<ComboBoxItem>()
            .OrderBy(i => Math.Abs((int)i.Tag - current)).First();
        TopmostCheck.IsChecked = app.Store.Settings.AlwaysOnTop;
        StartupCheck.IsChecked = RunAtLogin.IsEnabled();
        _loading = false;
    }

    private void Accounts_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateEmpty();

    private void UpdateEmpty()
        => NoAccountsText.Visibility = _app.Accounts.Any() ? Visibility.Collapsed : Visibility.Visible;

    protected override void OnClosed(EventArgs e)
    {
        _signInCts?.Cancel();
        _app.Accounts.CollectionChanged -= Accounts_CollectionChanged;
        _app.SaveStore();
        base.OnClosed(e);
    }

    private void Label_LostFocus(object sender, RoutedEventArgs e) => _app.SaveStore();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountViewModel vm) return;
        var r = MessageBox.Show(this, $"Stop monitoring “{vm.Label}” and delete its saved sign-in?",
            "Remove account", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) _app.RemoveAccount(vm);
    }

    private async void SignInClaude_Click(object sender, RoutedEventArgs e) => await RunSignIn(_app.Claude);
    private async void SignInCodex_Click(object sender, RoutedEventArgs e) => await RunSignIn(_app.Codex);

    private async Task RunSignIn(UsageProvider provider)
    {
        _signInCts?.Cancel();
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = _signInCts.Token;
        ShowStatus($"Finish signing in to {(provider.Kind == Provider.Claude ? "Claude" : "ChatGPT")} in your browser… " +
                   "Tip: use a private window if you want a different account than the one you're signed in with.", cancellable: true);
        AddButtons.IsEnabled = false;
        try
        {
            var account = await provider.SignInAsync(ct);
            _app.AddOrUpdateAccount(account);
            ShowStatus($"Added {account.Email ?? account.Label}.", cancellable: false);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Sign-in canceled or timed out.", cancellable: false);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, cancellable: false);
        }
        finally
        {
            AddButtons.IsEnabled = true;
        }
    }

    private void LinkClaude_Click(object sender, RoutedEventArgs e) => Link(_app.Claude);
    private void LinkCodex_Click(object sender, RoutedEventArgs e) => Link(_app.Codex);

    private void Link(UsageProvider provider)
    {
        try
        {
            var account = provider.LinkCli();
            _app.AddOrUpdateAccount(account);
            ShowStatus($"Linked to the {(provider.Kind == Provider.Claude ? "Claude Code" : "Codex CLI")} sign-in.", cancellable: false);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, cancellable: false);
        }
    }

    private void ShowStatus(string text, bool cancellable)
    {
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = text;
        CancelButton.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _signInCts?.Cancel();

    private void RefreshCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => Setting_Click(sender, e);

    private void Setting_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _app.Store.Settings;
        if (RefreshCombo.SelectedItem is ComboBoxItem { Tag: int minutes }) s.RefreshMinutes = minutes;
        s.AlwaysOnTop = TopmostCheck.IsChecked == true;
        _app.ApplySettings();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { RunAtLogin.Set(StartupCheck.IsChecked == true); }
        catch (Exception ex)
        {
            StartupCheck.IsChecked = RunAtLogin.IsEnabled();
            ShowStatus($"Couldn't change the startup setting: {ex.Message}", cancellable: false);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
