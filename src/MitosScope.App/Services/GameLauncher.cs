using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using MitosScope.App.Settings;
using MitosScope.Core.Discovery;

namespace MitosScope.App.Services;

/// <summary>
/// Remembers where the game is installed (the executable of the last client whose window became
/// known, from its HWND) and starts new instances of it with "-slotN" (tray "Open Instance").
/// </summary>
public sealed class GameLauncher
{
    public const int SlotCount = 20;

    private readonly AppSettings _settings;

    public GameLauncher(AppSettings settings) => _settings = settings;

    /// <summary>The saved game executable, if it still exists.</summary>
    public string? GamePath => _settings.GamePath is { } path && File.Exists(path) ? path : null;

    /// <summary>Raised when <see cref="GamePath"/> changes.</summary>
    public event Action? GamePathChanged;

    /// <summary>Saves the executable of the process that owns <paramref name="hwnd"/> (a game window).</summary>
    public void RememberFromWindow(long hwnd)
    {
        if (WindowOwner.FindProcessId(hwnd) is not { } pid || ExecutableOf(pid) is not { } path)
            return;
        if (string.Equals(path, _settings.GamePath, StringComparison.OrdinalIgnoreCase))
            return;
        _settings.GamePath = path;
        SettingsStore.Save(_settings);
        GamePathChanged?.Invoke();
    }

    /// <summary>Starts the game with "-slot<paramref name="slot"/>" (0-based).</summary>
    public void Launch(int slot)
    {
        if (GamePath is not { } path)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path, [$"-slot{slot.ToString()}"])
            {
                WorkingDirectory = Path.GetDirectoryName(path),
                UseShellExecute = false,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Trace.WriteLine($"[MitosScope] Failed to start the game: {ex.Message}");
        }
    }

    private static string? ExecutableOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null; // exited, or not accessible
        }
    }
}
