using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AIMeter.Core;
using AIMeter.UI;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AIMeter;

public partial class App : Application
{
    private const string MutexName = @"Local\AIMeter.SingleInstance";
    private const string ShowEventName = @"Local\AIMeter.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private MainWindow? _widget;
    private AccountsWindow? _accountsWindow;
    private readonly System.Collections.Generic.Dictionary<AccountViewModel, DetailWindow> _detailWindows = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _tickTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _refreshing;
    private bool _exiting;
    private DateTimeOffset? _lastRefresh;

    public AccountStore Store { get; } = new();
    public ObservableCollection<AccountViewModel> Accounts { get; } = new();
    public ClaudeProvider Claude { get; } = new();
    public CodexProvider Codex { get; } = new();
    public ImageSource? WindowIcon { get; private set; }

    private UsageProvider ProviderFor(Account a) => a.Provider == Provider.Claude ? Claude : Codex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            // Already running: ask that instance to show its widget, then quit.
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(ShowWidget);
        }) { IsBackground = true, Name = "AIMeter.ShowListener" }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        Store.Load();
        foreach (var a in Store.Accounts) Accounts.Add(new AccountViewModel(a));

        _icon = AppIcon.Create();
        WindowIcon = AppIcon.ToImageSource(_icon);
        CreateTray();

        _widget = new MainWindow(this);
        _widget.Show();

        _refreshTimer.Tick += async (_, _) => await RefreshAllAsync();
        _tickTimer.Tick += (_, _) => { foreach (var vm in Accounts) vm.Tick(); };
        _tickTimer.Start();
        SystemEvents.PowerModeChanged += async (_, args) =>
        {
            if (args.Mode == PowerModes.Resume) await Dispatcher.InvokeAsync(RefreshAllAsync).Task.Unwrap();
        };

        ApplySettings();
        _ = RefreshAllAsync();
        if (Accounts.Count == 0) ShowAccountsWindow();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show widget", null, (_, _) => ShowWidget());
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAllAsync());
        menu.Items.Add("Accounts && settings…", null, (_, _) => ShowAccountsWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "AI Meter", Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleWidget();
        };
    }

    public void ShowWidget()
    {
        if (_widget is null) return;
        _widget.Show();
        _widget.Activate();
    }

    private void ToggleWidget()
    {
        if (_widget is null) return;
        if (_widget.IsVisible) _widget.Hide();
        else ShowWidget();
    }

    public void ShowAccountsWindow()
    {
        if (_accountsWindow is null)
        {
            _accountsWindow = new AccountsWindow(this);
            _accountsWindow.Closed += (_, _) => _accountsWindow = null;
            _accountsWindow.Show();
        }
        if (_accountsWindow.WindowState == WindowState.Minimized) _accountsWindow.WindowState = WindowState.Normal;
        _accountsWindow.Activate();
    }

    public void ShowDetails(AccountViewModel vm)
    {
        if (!_detailWindows.TryGetValue(vm, out var window))
        {
            window = new DetailWindow(this, vm);
            window.Closed += (_, _) => _detailWindows.Remove(vm);
            _detailWindows[vm] = window;
            window.Show();
        }
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Moves an account to a new position in the widget and remembers the order.</summary>
    public void MoveAccount(AccountViewModel vm, int newIndex)
    {
        var oldIndex = Accounts.IndexOf(vm);
        newIndex = Math.Clamp(newIndex, 0, Accounts.Count - 1);
        if (oldIndex < 0 || oldIndex == newIndex) return;
        Accounts.Move(oldIndex, newIndex);
    }

    /// <summary>Writes the widget's current tile order to disk.</summary>
    public void SaveAccountOrder()
    {
        var ordered = Accounts.Select(v => v.Account).ToList();
        if (ordered.SequenceEqual(Store.Accounts)) return;
        Store.Accounts.Clear();
        Store.Accounts.AddRange(ordered);
        SaveStore();
    }

    public async Task RefreshAllAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        _widget?.SetUpdated(_lastRefresh, refreshing: true);
        try
        {
            await Task.WhenAll(Accounts.ToList().Select(RefreshOneAsync));
            _lastRefresh = DateTimeOffset.Now;
        }
        finally
        {
            _refreshing = false;
            _widget?.SetUpdated(_lastRefresh, refreshing: false);
            UpdateTrayText();
            PersistRefreshedTokens();
        }
    }

    private async Task RefreshOneAsync(AccountViewModel vm)
    {
        vm.SetLoading(true);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var snapshot = await ProviderFor(vm.Account).FetchAsync(vm.Account, cts.Token);
            vm.Apply(snapshot);
        }
        catch (AuthExpiredException ex) { vm.ApplyError(ex.Message, needsSignIn: true); }
        catch (OperationCanceledException) { vm.ApplyError("Timed out reaching the service; will retry on the next refresh.", false); }
        catch (HttpRequestException ex) { vm.ApplyError($"Network problem: {ex.Message}", false); }
        catch (Exception ex)
        {
            Log(ex);
            vm.ApplyError(ex.Message, false);
        }
        finally
        {
            vm.SetLoading(false);
        }
    }

    private void PersistRefreshedTokens()
    {
        if (!Claude.TokensChanged && !Codex.TokensChanged) return;
        Claude.TokensChanged = Codex.TokensChanged = false;
        SaveStore();
    }

    public void AddOrUpdateAccount(Account account)
    {
        var existing = Accounts.FirstOrDefault(v =>
            v.Account.Provider == account.Provider && v.Account.Source == account.Source &&
            (account.Source != AccountSource.OAuth ||
             (account.Email is not null && string.Equals(v.Account.Email, account.Email, StringComparison.OrdinalIgnoreCase))));

        if (existing is not null)
        {
            existing.Account.Tokens = account.Tokens;
            existing.Account.Email = account.Email ?? existing.Account.Email;
            existing.RefreshIdentity();
            SaveStore();
            _ = RefreshAndUpdateTrayAsync(existing);
            return;
        }

        if (account.Email is { } email && Accounts.Any(v => v.Account.Provider == account.Provider))
            account.Label = $"{account.Label} ({email})";

        Store.Accounts.Add(account);
        var vm = new AccountViewModel(account);
        Accounts.Add(vm);
        SaveStore();
        _ = RefreshAndUpdateTrayAsync(vm);
    }

    public async Task RefreshAndUpdateTrayAsync(AccountViewModel vm)
    {
        await RefreshOneAsync(vm);
        UpdateTrayText();
        PersistRefreshedTokens();
    }

    public void RemoveAccount(AccountViewModel vm)
    {
        if (_detailWindows.TryGetValue(vm, out var detail)) detail.Close();
        Store.Accounts.Remove(vm.Account);
        Accounts.Remove(vm);
        SaveStore();
        UpdateTrayText();
    }

    public async Task ReauthenticateAsync(AccountViewModel vm)
    {
        vm.ApplyError("Finish signing in in your browser…", needsSignIn: false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var fresh = await ProviderFor(vm.Account).SignInAsync(cts.Token);
            vm.Account.Tokens = fresh.Tokens;
            vm.Account.Email = fresh.Email ?? vm.Account.Email;
            SaveStore();
            await RefreshAndUpdateTrayAsync(vm);
        }
        catch (OperationCanceledException) { vm.ApplyError("Sign-in timed out.", needsSignIn: true); }
        catch (Exception ex) { vm.ApplyError(ex.Message, needsSignIn: true); }
    }

    public void ApplySettings()
    {
        var s = Store.Settings;
        _widget?.ApplyTopmost(s.AlwaysOnTop);
        _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, s.RefreshMinutes));
        _refreshTimer.Stop();
        _refreshTimer.Start();
        SaveStore();
    }

    public void SaveStore()
    {
        try { Store.Save(); }
        catch (Exception ex) { Log(ex); }
    }

    private void UpdateTrayText()
    {
        if (_tray is null) return;
        var lines = Accounts
            .Where(v => v.LowestRemaining is not null)
            .Select(v => $"{v.Label}: {v.LowestRemaining:0}% left");
        var text = string.Join("\n", new[] { "AI Meter" }.Concat(lines));
        _tray.Text = text.Length > 127 ? text[..126] + "…" : text;
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        SaveStore();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    internal static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AccountStore.Folder);
            File.AppendAllText(Path.Combine(AccountStore.Folder, "error.log"),
                $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}", System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
