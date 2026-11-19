using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace MitosScope.App.Services;

/// <summary>
/// Event-driven notification of a game window being dragged around (windowed mode), via
/// SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE) on that window's process. Only pure moves are
/// reported: a size change (resize, maximize, fullscreen toggle) and minimizing just update the
/// reference rectangle, so the panels don't jump. Windows only.
/// Must be used on the UI thread (the hook callback is delivered through its message loop).
/// </summary>
public sealed partial class GameWindowWatcher : IDisposable
{
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);

    private WinEventProc? _proc; // kept in a field so the GC doesn't collect the callback
    private nint _hook;
    private nint _window;
    private RECT _rect;

    /// <summary>Raised with the offset (screen pixels) the watched window just moved by.</summary>
    public event Action<PixelPoint>? Moved;

    /// <summary>Watches <paramref name="hwnd"/> from now on (a different window replaces the previous one).</summary>
    public void Watch(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0 || (nint)hwnd == _window)
            return;
        Unhook();
        _ = GetWindowThreadProcessId((nint)hwnd, out var pid);
        if (pid == 0 || !GetWindowRect((nint)hwnd, out _rect))
            return;
        _window = (nint)hwnd;
        _proc ??= OnWinEvent;
        _hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, 0,
            Marshal.GetFunctionPointerForDelegate(_proc), pid, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != _window || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
            return;
        if (IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect))
            return; // minimized: keep the last real position
        var old = _rect;
        _rect = rect;
        var sameSize = rect.Right - rect.Left == old.Right - old.Left && rect.Bottom - rect.Top == old.Bottom - old.Top;
        if (sameSize && (rect.Left != old.Left || rect.Top != old.Top))
            Moved?.Invoke(new PixelPoint(rect.Left - old.Left, rect.Top - old.Top));
    }

    private void Unhook()
    {
        if (_hook != 0)
            UnhookWinEvent(_hook);
        _hook = 0;
        _window = 0;
    }

    public void Dispose() => Unhook();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        nint lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(nint hWinEventHook);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);
}
