using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace IdmClone;

/// <summary>
/// Windows lets a person switch off some of its own Win + letter shortcuts (the list "DisabledHotkeys" in the user's Explorer
/// settings). Win + F opens the Feedback Hub: this switches that off (or on again) for the current user only. It takes effect
/// the next time Windows Explorer starts (sign out and in again). Other letters that are already in the list are left alone.
/// </summary>
public static class ExplorerHotkeys
{
    /// <summary>Where the list lives (changeable so a test does not touch the real setting).</summary>
    public static string SubKey { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ValueName = "DisabledHotkeys";

    private static string Current()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SubKey);
            return key?.GetValue(ValueName) as string ?? "";
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return ""; }
    }

    public static bool IsDisabled(char letter) => Current().IndexOf(char.ToUpperInvariant(letter)) >= 0;

    /// <summary>Switches Windows' own Win + <paramref name="letter"/> off (true) or back on (false). Returns false if Windows would not let us.</summary>
    public static bool Set(char letter, bool disabled)
    {
        char c = char.ToUpperInvariant(letter);
        var letters = Current().ToUpperInvariant().Distinct().ToList();
        if (disabled && !letters.Contains(c)) letters.Add(c);
        if (!disabled) letters.Remove(c);
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SubKey);
            if (letters.Count == 0) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else key.SetValue(ValueName, new string(letters.ToArray()), RegistryValueKind.String);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }
}
