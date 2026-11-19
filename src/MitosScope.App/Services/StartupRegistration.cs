using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MitosScope.App.Services;

/// <summary>
/// "Start with Windows": a value in HKCU\...\CurrentVersion\Run pointing to this exe (the exe that
/// turned it on). The registry is the source of truth, nothing is kept in settings. Windows only.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MitosScope";

    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => OperatingSystem.IsWindows() && Environment.ProcessPath is not null;

    public static bool IsEnabled
    {
        get
        {
            if (!IsSupported)
                return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!IsSupported)
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            System.Diagnostics.Trace.WriteLine($"[MitosScope] Failed to change start with Windows: {ex.Message}");
        }
    }
}
