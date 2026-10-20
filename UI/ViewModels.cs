using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AIMeter.Core;

namespace AIMeter.UI;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

internal static class Palette
{
    public static readonly SolidColorBrush Claude = Freeze("#D97757");
    public static readonly SolidColorBrush Codex = Freeze("#3FB98A");
    public static readonly SolidColorBrush Ok = Freeze("#4CC38A");
    public static readonly SolidColorBrush Warn = Freeze("#E8B64C");
    public static readonly SolidColorBrush Danger = Freeze("#F06A6A");

    public static SolidColorBrush ForUsage(double pct) => pct >= 90 ? Danger : pct >= 70 ? Warn : Ok;

    public static SolidColorBrush Freeze(string hex, byte? alpha = null)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex);
        if (alpha is { } a) c.A = a;
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public sealed class MeterViewModel : Observable
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly UsageMeter _m;

    public MeterViewModel(UsageMeter m) => _m = m;

    public string Name => _m.Name;
    public double PercentUsed => _m.PercentUsed;
    public double Fraction => Math.Clamp(_m.PercentUsed / 100.0, 0, 1);
    public Brush BarBrush => Palette.ForUsage(_m.PercentUsed);
    public string RemainingText => _m.PercentUsed >= 100 ? "Limit reached" : $"{Math.Max(0, 100 - _m.PercentUsed):0}% left";

    public string SubText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string> { $"{_m.PercentUsed:0}% used" };
            if (_m.Detail is { } d) parts.Add(d);
            if (_m.ResetsAt is { } r) parts.Add("resets " + FormatReset(r));
            return string.Join(" · ", parts);
        }
    }

    // Detail view
    public string UsedText => $"{_m.PercentUsed:0.#}%";
    public string LeftText => $"{Math.Max(0, 100 - _m.PercentUsed):0.#}%";
    public string? Detail => _m.Detail;
    public Visibility DetailVisibility => string.IsNullOrEmpty(_m.Detail) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ResetVisibility => _m.ResetsAt is null ? Visibility.Collapsed : Visibility.Visible;
    public string ResetAtText => _m.ResetsAt?.ToLocalTime().ToString("dddd, MMM d 'at' h:mm tt", En) ?? "";
    public string ResetInText => _m.ResetsAt is { } r ? FormatRelative(r - DateTimeOffset.Now) : "";

    public void Tick()
    {
        Raise(nameof(SubText));
        Raise(nameof(ResetInText));
    }

    private static string FormatRelative(TimeSpan span)
        => span <= TimeSpan.Zero ? "now"
         : span.TotalDays >= 1 ? $"in {(int)span.TotalDays}d {span.Hours}h"
         : span.TotalHours >= 1 ? $"in {(int)span.TotalHours}h {span.Minutes}m"
         : $"in {Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";

    internal static string FormatReset(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        var span = at - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "now";
        var abs = local.Date == DateTime.Today ? local.ToString("h:mm tt", En)
                : local.Date == DateTime.Today.AddDays(1) ? "tomorrow " + local.ToString("h:mm tt", En)
                : local.ToString("ddd h:mm tt", En);
        return $"{FormatRelative(span)} ({abs})";
    }
}

public sealed class AccountViewModel : Observable
{
    public AccountViewModel(Account account) => Account = account;

    public Account Account { get; }

    public string Label
    {
        get => Account.Label;
        set { if (Account.Label != value) { Account.Label = value; Raise(); } }
    }

    public string ProviderName => Account.Provider == Provider.Claude ? "Claude" : "Codex";
    public Brush AccentBrush => Account.Provider == Provider.Claude ? Palette.Claude : Palette.Codex;
    public Brush PlanBackground => Palette.Freeze(Account.Provider == Provider.Claude ? "#D97757" : "#3FB98A", 0x26);

    public string SourceText => Account.Source switch
    {
        AccountSource.ClaudeCli => "linked to Claude Code",
        AccountSource.CodexCli => "linked to Codex CLI",
        _ => "signed in",
    };

    public string ManageSubtitle => string.Join(" · ", new[] { ProviderName, SourceText, Account.Email }.Where(s => !string.IsNullOrEmpty(s)));

    public ObservableCollection<MeterViewModel> Meters { get; } = new();

    public string? Plan { get; private set; }
    public string? Status { get; private set; }
    public string? Notes { get; private set; }
    public string? Error { get; private set; }
    public bool NeedsSignIn { get; private set; }
    public bool IsLoading { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public string Subtitle
    {
        get
        {
            if (IsLoading && UpdatedAt is null) return "Loading…";
            var bits = new[] { Account.Email, Status }.Where(s => !string.IsNullOrEmpty(s));
            var s = string.Join(" · ", bits);
            return s.Length > 0 ? s : ProviderName;
        }
    }

    public Visibility PlanVisibility => string.IsNullOrEmpty(Plan) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NotesVisibility => string.IsNullOrEmpty(Notes) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SignInVisibility => NeedsSignIn && Account.Source == AccountSource.OAuth ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoMetersVisibility => Meters.Count == 0 && Error is null ? Visibility.Visible : Visibility.Collapsed;

    public string UpdatedText => IsLoading ? "Updating…"
        : UpdatedAt is { } t ? t.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"))
        : "Not yet";

    private bool _isDragging;
    /// <summary>True while this tile is being dragged to a new position in the widget.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set { if (_isDragging != value) { _isDragging = value; Raise(); Raise(nameof(TileOpacity)); } }
    }
    public double TileOpacity => IsDragging ? 0.45 : 1.0;

    /// <summary>Smallest remaining percentage across meters, for the tray tooltip.</summary>
    public double? LowestRemaining => Meters.Count == 0 ? null : Meters.Min(m => Math.Max(0, 100 - m.PercentUsed));

    public void SetLoading(bool loading)
    {
        IsLoading = loading;
        Raise(nameof(IsLoading));
        Raise(nameof(Subtitle));
        Raise(nameof(UpdatedText));
    }

    public void Apply(UsageSnapshot s)
    {
        if (s.Email is { Length: > 0 } email) Account.Email = email;
        Plan = s.Plan;
        Status = s.Status;
        Notes = s.Notes.Count == 0 ? null : string.Join("\n", s.Notes);
        Error = null;
        NeedsSignIn = false;
        UpdatedAt = DateTimeOffset.Now;
        Meters.Clear();
        foreach (var m in s.Meters) Meters.Add(new MeterViewModel(m));
        RaiseAll();
    }

    public void ApplyError(string message, bool needsSignIn)
    {
        Error = message;
        NeedsSignIn = needsSignIn;
        if (needsSignIn) Meters.Clear();
        RaiseAll();
    }

    public void Tick()
    {
        foreach (var m in Meters) m.Tick();
    }

    public void RefreshIdentity() => RaiseAll();
}
