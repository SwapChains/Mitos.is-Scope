using System.Runtime.InteropServices;

namespace MitosScope.Core.Discovery;

/// <summary>Which process owns a native window (the game window from /v1/hwnd). Windows only.</summary>
public static partial class WindowOwner
{
    /// <summary>PID of the process that owns <paramref name="hwnd"/>; null if it's not a window (any more) or not Windows.</summary>
    public static int? FindProcessId(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0 || !IsWindow((nint)hwnd))
            return null;
        _ = GetWindowThreadProcessId((nint)hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    /// <summary>
    /// The top-level window at the root of <paramref name="hwnd"/>'s parent/owner chain (itself if it
    /// already is one), the same thing the foreground watcher reports. Unchanged outside Windows.
    /// </summary>
    public static long RootOwner(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0 || !IsWindow((nint)hwnd))
            return hwnd;
        var root = GetAncestor((nint)hwnd, GA_ROOTOWNER);
        return root != 0 ? root : hwnd;
    }

    private const uint GA_ROOTOWNER = 3;

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint gaFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
