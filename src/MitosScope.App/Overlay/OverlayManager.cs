using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using MitosScope.App.Settings;
using MitosScope.App.ViewModels;
using MitosScope.App.ViewModels.Panels;

namespace MitosScope.App.Overlay;

/// <summary>
/// Owns one borderless, top-most window per panel ("MDI" over the whole desktop), switches them
/// between edit mode (move/resize) and locked mode (click-through), and persists their layout.
///
/// A panel window is shown only when all of these hold:
/// - the panel is enabled (closed with its ✕ / unchecked in the tray → saved in settings);
/// - the panel is set to show in the current game state, menu or match (see MatchStateController);
///   edit mode ignores this so every enabled panel can be arranged;
/// - the overlay isn't hidden as a whole (tray "Show overlay");
/// - the focus allows it (a game client is focused, or edit mode is on — see FocusController).
/// It also owns the popup windows, the big cards opened by clickable panels (<see cref="AttachPopups"/>).
/// Must be used on the UI thread.
/// </summary>
public sealed class OverlayManager
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, PanelWindow> _windows = [];
    private bool _focusVisible = true;
    private bool _inMatch;

    public OverlayManager(AppSettings settings, IEnumerable<PanelViewModel> panels)
    {
        _settings = settings;
        Panels = panels.ToList();
    }

    public IReadOnlyList<PanelViewModel> Panels { get; }

    public bool IsEditMode { get; private set; }

    /// <summary>False when the user hid the whole overlay from the tray.</summary>
    public bool IsOverlayShown { get; private set; } = true;

    public event Action<bool>? EditModeChanged;
    public event Action<bool>? OverlayShownChanged;
    public event Action<PanelViewModel, bool>? PanelEnabledChanged;

    /// <summary>(panel, inMatch, shown): a "Show in menu" / "Show in match" setting changed.</summary>
    public event Action<PanelViewModel, bool, bool>? PanelGameStateChanged;

    public void ShowAll()
    {
        foreach (var panel in Panels)
        {
            var layout = LayoutOf(panel);
            var window = new PanelWindow(panel)
            {
                Width = layout.Width,
                Height = layout.Height,
                Position = new PixelPoint(layout.X, layout.Y),
            };
            window.Opened += (_, _) =>
            {
                EnsureOnScreen(window, panel);
                ApplyInputMode(window);
                CaptureLayout(window);
                // Track the layout live: on exit the windows are already closed (and report 0,0).
                window.PositionChanged += (_, _) => CaptureLayout(window);
                window.SizeChanged += (_, _) => CaptureLayout(window);
            };
            window.CloseRequested += () => SetPanelEnabled(panel, false);
            window.Clicked += () => _lastClicked = window;
            _windows[panel.Id] = window;
        }
        ApplyVisibility();

        _hotButtonsTimer.Tick += (_, _) => UpdateHotButtons();
        _hotButtonsTimer.Start();
    }

    // ---- visibility -------------------------------------------------------------------------

    public bool IsPanelEnabled(PanelViewModel panel) => LayoutOf(panel).Visible;

    /// <summary>Enables/disables one panel and saves it (✕ button or tray checkbox).</summary>
    public void SetPanelEnabled(PanelViewModel panel, bool enabled)
    {
        var layout = LayoutOf(panel);
        if (layout.Visible == enabled)
            return;
        layout.Visible = enabled;
        _settings.Panels[panel.Id] = layout;
        SettingsStore.Save(_settings);
        ApplyVisibility();
        PanelEnabledChanged?.Invoke(panel, enabled);
    }

    /// <summary>Whether the panel shows in the menu (<paramref name="inMatch"/> false) or in a match (true).</summary>
    public bool IsShownIn(PanelViewModel panel, bool inMatch) =>
        inMatch ? LayoutOf(panel).ShowInMatch : LayoutOf(panel).ShowInMenu;

    /// <summary>Changes "Show in menu" / "Show in match" for one panel and saves it.</summary>
    public void SetShownIn(PanelViewModel panel, bool inMatch, bool shown)
    {
        var layout = LayoutOf(panel);
        if (IsShownIn(panel, inMatch) == shown)
            return;
        if (inMatch)
            layout.ShowInMatch = shown;
        else
            layout.ShowInMenu = shown;
        _settings.Panels[panel.Id] = layout;
        SettingsStore.Save(_settings);
        ApplyVisibility();
        PanelGameStateChanged?.Invoke(panel, inMatch, shown);
    }

    /// <summary>Set by MatchStateController: true while the displayed client is in a match.</summary>
    public void SetInMatch(bool inMatch)
    {
        if (_inMatch == inMatch)
            return;
        _inMatch = inMatch;
        if (inMatch)
            foreach (var window in _popups.Where(w => w.Popup.CloseInMatch))
                window.Popup.Close(); // a big card would cover the game
        ApplyVisibility();
    }

    public void ToggleOverlayShown() => SetOverlayShown(!IsOverlayShown);

    public void SetOverlayShown(bool shown)
    {
        if (IsOverlayShown == shown)
            return;
        IsOverlayShown = shown;
        ApplyVisibility();
        OverlayShownChanged?.Invoke(shown);
    }

    /// <summary>Set by FocusController: whether the current foreground allows the overlay.</summary>
    public void SetFocusVisible(bool visible)
    {
        if (_focusVisible == visible)
            return;
        _focusVisible = visible;
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        foreach (var panel in Panels)
        {
            var window = _windows[panel.Id];
            var show = _focusVisible && IsOverlayShown && IsPanelEnabled(panel)
                       && (IsEditMode || IsShownIn(panel, _inMatch));
            if (show && !window.IsVisible)
                window.Show(); // ShowActivated=False: doesn't steal focus from the game
            else if (!show && window.IsVisible)
                window.Hide();
        }
        ApplyPopupsVisibility();
    }

    // ---- edit mode --------------------------------------------------------------------------

    public void ToggleEditMode() => SetEditMode(!IsEditMode);

    public void SetEditMode(bool enabled)
    {
        if (IsEditMode == enabled)
            return;
        IsEditMode = enabled;

        foreach (var window in _windows.Values)
        {
            window.Panel.IsEditMode = enabled;
            ApplyInputMode(window);
        }

        if (!enabled)
        {
            // leaving edit mode = layout confirmed: the distances from the game window are taken
            // from where the panels are now (before hiding windows: CaptureLayout skips hidden ones)
            foreach (var window in _windows.Values.Where(w => w.IsVisible))
                RememberOffset(window);
            SaveLayout();
        }

        ApplyVisibility(); // edit mode shows every enabled panel, whatever the game state
        if (!enabled)
            PlaceInGameWindow(); // back inside the active instance

        EditModeChanged?.Invoke(enabled);
    }

    /// <summary>
    /// Edit mode: normal window (drag/resize). Locked: click-through, except interactive panels,
    /// which take clicks; neither ever takes the focus from the game.
    /// </summary>
    private void ApplyInputMode(PanelWindow window)
    {
        if (IsEditMode)
            WindowInterop.SetInputMode(window, clickThrough: false, noActivate: false);
        else
            WindowInterop.SetInputMode(window, clickThrough: !window.Panel.IsInteractive && !window.IsHot, noActivate: true);
    }

    // ---- hot buttons: small buttons inside click-through panels -------------------------------

    /// <summary>
    /// A click-through window gets no mouse events at all, so the mouse position is polled: while
    /// it's over an overlay button (class "overlayButton", e.g. Repair) that panel takes clicks,
    /// everywhere else they still go to the game.
    /// </summary>
    private readonly DispatcherTimer _hotButtonsTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

    private void UpdateHotButtons()
    {
        if (IsEditMode || !WindowInterop.TryGetCursorPos(out var cursor))
            return;

        foreach (var window in _windows.Values)
        {
            if (window.Panel.IsInteractive)
                continue; // already takes every click
            var hot = window.IsOverOverlayButton(cursor);
            if (hot == window.IsHot)
                continue;
            window.IsHot = hot;
            ApplyInputMode(window);
        }
    }

    // ---- popups (big cards in the middle of the screen) ----------------------------------------

    private readonly List<PopupWindow> _popups = [];

    /// <summary>The last interactive panel clicked: popups open on its screen.</summary>
    private PanelWindow? _lastClicked;

    /// <summary>
    /// Creates the windows of the popups (see PopupViewModel). Each one shows in the middle of the
    /// screen while open and the overlay is visible. Only one is open at a time, and those with
    /// CloseInMatch are closed when the player enters a match.
    /// </summary>
    public void AttachPopups(IEnumerable<PopupViewModel> popups)
    {
        foreach (var popup in popups)
        {
            var window = new PopupWindow(popup);
            window.Opened += (_, _) => WindowInterop.SetInputMode(window, clickThrough: false, noActivate: true);
            popup.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(PopupViewModel.IsOpen))
                    return;
                if (popup.IsOpen)
                    foreach (var other in _popups.Where(o => o.Popup != popup))
                        other.Popup.Close();
                ApplyVisibility();
            };
            _popups.Add(window);
        }
        ApplyVisibility();
    }

    private void ApplyPopupsVisibility()
    {
        foreach (var window in _popups)
        {
            var show = window.Popup.IsOpen && _focusVisible && IsOverlayShown;
            if (show && !window.IsVisible)
            {
                Center(window);
                window.Show(); // ShowActivated=False: doesn't steal focus from the game
            }
            else if (!show && window.IsVisible)
                window.Hide();
        }
    }

    /// <summary>Middle of the screen of the panel that opened it (the primary screen as a fallback).</summary>
    private void Center(PopupWindow window)
    {
        var anchor = _lastClicked is { IsVisible: true } p ? p : _windows.Values.FirstOrDefault(w => w.IsVisible);
        var screen = (anchor is not null ? anchor.Screens.ScreenFromWindow(anchor) : null) ?? window.Screens.Primary;
        if (screen is null)
            return;
        var area = screen.WorkingArea;
        var w = (int)(window.Popup.Width * screen.Scaling);
        var h = (int)(window.Popup.Height * screen.Scaling);
        window.Position = new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2);
    }

    // ---- panels inside the active game window ------------------------------------------------
    //
    // Each panel remembers its distance from the nearest corner of the game window (in DIPs, so
    // it's the same on any DPI) and is placed at that distance inside whichever instance is
    // active: when the game starts, when another instance gets the focus, when switching back,
    // and when the active window is dragged or resized. Arranging the panels in edit mode
    // updates the distances. Panels never go outside the game window.

    /// <summary>The game window the panels are inside now (0 = none yet).</summary>
    private long _gameWindow;

    /// <summary>The game area of <see cref="_gameWindow"/> the last time the panels were placed.</summary>
    private PixelRect _gameArea;

    /// <summary>Set while this class moves the panels, so their PositionChanged doesn't count as the player arranging them.</summary>
    private bool _placing;

    /// <summary>
    /// <paramref name="hwnd"/> is the active game window: put every panel inside it, at its
    /// remembered distance from the window's corner. Called on every focus of a game window.
    /// </summary>
    public void AttachToGameWindow(long hwnd)
    {
        if (hwnd == 0)
            return;
        if (IsEditMode)
        {
            // Arranging: keep the panels where the player puts them, but measure them against this window.
            _gameWindow = hwnd;
            if (Services.GameWindowWatcher.TryGetClientRect(hwnd, out var area))
                _gameArea = area;
            return;
        }
        if (hwnd == _gameWindow && Services.GameWindowWatcher.TryGetClientRect(hwnd, out var same) && same == _gameArea)
            return; // same window, same place: nothing to do
        _gameWindow = hwnd;
        PlaceInGameWindow();
    }

    /// <summary>
    /// Tray "Reset panels": every panel enabled again, with its default size, menu/match options
    /// and place inside the game window (PanelViewModel.DefaultLayout). Saved right away.
    /// </summary>
    public void ResetPanels()
    {
        _placing = true;
        try
        {
            foreach (var panel in Panels)
            {
                var layout = panel.DefaultLayout; // a new instance each time
                layout.Visible = true;
                _settings.Panels[panel.Id] = layout;

                var window = _windows[panel.Id];
                window.Width = layout.Width;
                window.Height = layout.Height;
                window.Position = new PixelPoint(layout.X, layout.Y); // until a game window is known

                PanelEnabledChanged?.Invoke(panel, true);
                PanelGameStateChanged?.Invoke(panel, false, layout.ShowInMenu);
                PanelGameStateChanged?.Invoke(panel, true, layout.ShowInMatch);
            }
        }
        finally
        {
            _placing = false;
        }

        ApplyVisibility();
        PlaceInGameWindow(); // inside the active instance (no-op if no game window is known yet)
        SaveLayout();
    }

    /// <summary>The active game window moved or changed size: the panels move with it.</summary>
    public void GameWindowChanged()
    {
        if (IsEditMode)
        {
            if (Services.GameWindowWatcher.TryGetClientRect(_gameWindow, out var area))
                _gameArea = area;
            return;
        }
        PlaceInGameWindow();
    }

    private void PlaceInGameWindow()
    {
        if (!Services.GameWindowWatcher.TryGetClientRect(_gameWindow, out var area))
            return; // minimized or gone: keep the last place
        _gameArea = area;
        var scaling = ScalingAt(area);

        _placing = true;
        try
        {
            foreach (var window in _windows.Values)
            {
                var layout = LayoutOf(window.Panel);
                if (!layout.HasGameOffset)
                    RememberOffset(layout, new PixelPoint(layout.X, layout.Y), area, scaling); // first time: keep where it is, as offset

                var w = (int)Math.Round(layout.Width * scaling);
                var h = (int)Math.Round(layout.Height * scaling);
                var dx = (int)Math.Round(layout.GameOffsetX * scaling);
                var dy = (int)Math.Round(layout.GameOffsetY * scaling);
                // the reference point, as a percentage of the game window (layouts saved before the
                // percentages: the window's corner, as they were measured)
                var fx = layout.GameFractionX ?? (layout.GameAnchorRight ? 1 : 0);
                var fy = layout.GameFractionY ?? (layout.GameAnchorBottom ? 1 : 0);
                var px = area.X + (int)Math.Round(fx * area.Width);
                var py = area.Y + (int)Math.Round(fy * area.Height);
                var x = layout.GameAnchorRight ? px - w - dx : px + dx;
                var y = layout.GameAnchorBottom ? py - h - dy : py + dy;
                // inside the game window, even if it's smaller than when the panels were arranged
                x = Math.Max(area.X, Math.Min(x, area.Right - w));
                y = Math.Max(area.Y, Math.Min(y, area.Bottom - h));

                var position = new PixelPoint(x, y);
                // Hidden panels move too: CaptureLayout skips them, so the position is saved here.
                layout.X = x;
                layout.Y = y;
                _settings.Panels[window.Panel.Id] = layout;
                if (window.Position != position)
                    window.Position = position;
            }
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>
    /// Stores where a panel is relative to the game area, as a percentage of it: the edge nearest to
    /// a side of the window (a panel on the right keeps its right edge at the same % of the width),
    /// so it stays in the same free spot of the game's UI on a bigger or smaller instance.
    /// </summary>
    private static void RememberOffset(PanelLayout layout, PixelPoint position, PixelRect area, double scaling)
    {
        var w = layout.Width * scaling;
        var h = layout.Height * scaling;
        var left = position.X - area.X;
        var top = position.Y - area.Y;
        var right = area.Right - (position.X + w);
        var bottom = area.Bottom - (position.Y + h);

        layout.GameAnchorRight = right < left;
        layout.GameAnchorBottom = bottom < top;
        var edgeX = layout.GameAnchorRight ? position.X + w - area.X : left;
        var edgeY = layout.GameAnchorBottom ? position.Y + h - area.Y : top;
        layout.GameFractionX = Math.Clamp(edgeX / Math.Max(1, area.Width), 0, 1);
        layout.GameFractionY = Math.Clamp(edgeY / Math.Max(1, area.Height), 0, 1);
        layout.GameOffsetX = 0;
        layout.GameOffsetY = 0;
        layout.HasGameOffset = true;
    }

    /// <summary>After the player moved a panel in edit mode: its new distance from the game window.</summary>
    private void RememberOffset(PanelWindow window)
    {
        if (_gameWindow == 0 || !Services.GameWindowWatcher.TryGetClientRect(_gameWindow, out var area))
            return;
        RememberOffset(LayoutOf(window.Panel), window.Position, area, ScalingAt(area));
    }

    /// <summary>DPI scaling of the screen the game window is on.</summary>
    private double ScalingAt(PixelRect area)
    {
        var probe = _windows.Values.FirstOrDefault();
        var screen = probe?.Screens.ScreenFromBounds(area) ?? probe?.Screens.Primary;
        return screen?.Scaling is > 0 and var s ? s : 1;
    }

    // ---- layout -----------------------------------------------------------------------------

    /// <summary>Writes the layout captured while the windows were open.</summary>
    public void SaveLayout() => SettingsStore.Save(_settings);

    private PanelLayout LayoutOf(PanelViewModel panel) =>
        _settings.Panels.GetValueOrDefault(panel.Id) ?? panel.DefaultLayout;

    private void CaptureLayout(PanelWindow window)
    {
        if (!window.IsVisible)
            return;
        // Moved by the player in edit mode (not by PlaceInGameWindow): new distance from the game window.
        if (IsEditMode && !_placing)
            RememberOffset(window);
        var layout = LayoutOf(window.Panel);
        layout.X = window.Position.X;
        layout.Y = window.Position.Y;
        layout.Width = window.Bounds.Width;
        layout.Height = window.Bounds.Height;
        _settings.Panels[window.Panel.Id] = layout;
    }

    /// <summary>A saved position can point to a monitor that no longer exists; fall back to the default.</summary>
    private static void EnsureOnScreen(PanelWindow window, PanelViewModel panel)
    {
        var center = window.Position + new PixelPoint((int)(window.Bounds.Width / 2), 10);
        if (window.Screens.ScreenFromPoint(center) is null)
            window.Position = new PixelPoint(panel.DefaultLayout.X, panel.DefaultLayout.Y);
    }
}
