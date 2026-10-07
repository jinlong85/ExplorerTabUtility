using System;
using System.IO;
using Microsoft.Win32;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

public static class RegistryManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ExplorerAdvancedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private static readonly string? ExecutablePath = Helper.GetExecutablePath();
    public static bool IsStartupEnabled => IsInStartup() && IsStartupApprovedEnabled();

    /// <summary>Raised after the startup setting was changed by this app (argument: the new effective state).</summary>
    public static event Action<bool>? StartupChanged;

    public static void ToggleStartup() => SetStartup(!IsStartupEnabled);

    public static void SetStartup(bool enable)
    {
        try
        {
            if (enable)
                AddToStartup();
            else
                RemoveFromStartup();
        }
        finally
        {
            StartupChanged?.Invoke(IsStartupEnabled);
        }
    }

    /// <summary>
    /// Keeps the "start with Windows" entry valid when the app was moved:
    /// if the Run value points to an executable that no longer exists, it is re-written to the current executable.
    /// An entry pointing to another existing copy of the app is left untouched.
    /// Old unquoted entries for the current executable are rewritten in the quoted form.
    /// </summary>
    public static void RepairStartupPath()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return;

        try
        {
            using var key = OpenCurrentUserKey(RunKeyPath, true);
            if (key?.GetValue(Constants.AppName) is not string value || string.IsNullOrWhiteSpace(value)) return;

            var registeredPath = ExtractExecutablePath(value);
            var isCurrentExecutable = IsSamePath(registeredPath, ExecutablePath!);

            if (isCurrentExecutable && value.TrimStart().StartsWith("\"")) return;
            if (!isCurrentExecutable && !string.IsNullOrWhiteSpace(registeredPath) && File.Exists(registeredPath)) return;

            key.SetValue(Constants.AppName, Quote(ExecutablePath!));
        }
        catch
        {
            // Best effort only; never block the app start because of the registry.
        }
    }

    private static bool IsInStartup()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return false;

        // Check if the application exists in the Run registry key and has the correct executable location
        using var key = OpenCurrentUserKey(RunKeyPath, false);
        if (key?.GetValue(Constants.AppName) is not string value) return false;
        return IsSamePath(ExtractExecutablePath(value), ExecutablePath!);
    }

    private static string Quote(string path) => $"\"{path}\"";

    /// <summary>Gets the executable path from a Run value: <c>"C:\a b\app.exe" --args</c> or <c>C:\a b\app.exe</c>.</summary>
    private static string ExtractExecutablePath(string value)
    {
        value = value.Trim();
        if (!value.StartsWith("\"")) return value;

        var closingQuote = value.IndexOf('"', 1);
        return closingQuote > 0 ? value.Substring(1, closingQuote - 1) : value.Trim('"');
    }

    private static bool IsSamePath(string? path1, string path2)
    {
        if (string.IsNullOrWhiteSpace(path1)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsStartupApprovedEnabled()
    {
        using var key = OpenCurrentUserKey(StartupApprovedKeyPath, false);
        var value = key?.GetValue(Constants.AppName) as byte[];
        // Check first byte parity (even = enabled, odd = disabled), null and empty also mean enabled.
        return value == null || value.Length == 0 || value[0] % 2 == 0;
    }

    private static void AddToStartup()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath)) return;

        // Add to Run registry key
        using var runKey = OpenCurrentUserKey(RunKeyPath, true);
        runKey?.SetValue(Constants.AppName, Quote(ExecutablePath!));

        // Create enabled entry in StartupApproved
        var enabledData = new byte[12];
        enabledData[0] = 0x02; // Even value for enabled

        using var approvedKey = OpenCurrentUserKey(StartupApprovedKeyPath, true);
        approvedKey?.SetValue(Constants.AppName, enabledData, RegistryValueKind.Binary);
    }

    private static void RemoveFromStartup()
    {
        // Remove from Run registry key
        using var runKey = OpenCurrentUserKey(RunKeyPath, true);
        runKey?.DeleteValue(Constants.AppName, false);

        // Remove from StartupApproved
        using var approvedKey = OpenCurrentUserKey(StartupApprovedKeyPath, true);
        approvedKey?.DeleteValue(Constants.AppName, false);
    }

    public static int GetDefaultExplorerLaunchId()
    {
        using var key = OpenCurrentUserKey(ExplorerAdvancedKeyPath, false);
        if (key == null) return 1;
        return key.GetValue("LaunchTo") as int? ?? 1;
    }

    private static RegistryKey? OpenCurrentUserKey(string name, bool writable) => Registry.CurrentUser.OpenSubKey(name, writable);
}