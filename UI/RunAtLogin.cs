using System;
using Microsoft.Win32;

namespace AIMeter.UI;

/// <summary>
/// Per-user "run at sign-in" via HKCU\...\Run (no admin rights needed). The installer creates the same
/// Run value; the user's choice is also recorded under <see cref="PrefKey"/> so upgrades respect it.
/// </summary>
internal static class RunAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIMeter";
    private const string PrefKey = @"Software\ViviScape\AIMeter";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(ValueName, $"\"{exe}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);

        using var pref = Registry.CurrentUser.CreateSubKey(PrefKey);
        pref.SetValue("AutoStart", enabled ? "1" : "0");
    }
}
