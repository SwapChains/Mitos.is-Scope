using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
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
            SaveLayout(); // leaving edit mode = layout confirmed (before hiding windows: CaptureLayout skips hidden ones)

        ApplyVisibility(); // edit mode shows every enabled panel, whatever the game state

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
            WindowInterop.SetInputMode(window, clickThrough: !window.Panel.IsInteractive, noActivate: true);
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

    // ---- layout -----------------------------------------------------------------------------

    /// <summary>Writes the layout captured while the windows were open.</summary>
    public void SaveLayout() => SettingsStore.Save(_settings);

    private PanelLayout LayoutOf(PanelViewModel panel) =>
        _settings.Panels.GetValueOrDefault(panel.Id) ?? panel.DefaultLayout;

    private void CaptureLayout(PanelWindow window)
    {
        if (!window.IsVisible)
            return;
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
