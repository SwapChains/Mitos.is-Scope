using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MitosScope.App.ViewModels;

/// <summary>
/// Base for a big card shown in the middle of the screen (see Overlay/PopupWindow), usually opened
/// by clicking a small interactive panel. Its view is resolved by ViewLocator
/// (XxxViewModel → Views/XxxView), like the panels.
///
/// To add one:
/// 1. Derive from this class (set <see cref="Title"/>, optionally the size and <see cref="CloseInMatch"/>,
///    and load data in <see cref="OnOpened"/>);
/// 2. Create Views/XxxView.axaml for it (a ✕ button can bind to <see cref="CloseCommand"/>);
/// 3. Register it in App.CreatePopups();
/// 4. Open it from a panel: override PanelViewModel.IsInteractive (true) and OnClicked (popup.Toggle()).
///
/// Only one popup is open at a time: opening one closes the others (OverlayManager).
/// Use on the UI thread.
/// </summary>
public abstract partial class PopupViewModel : ViewModelBase
{
    /// <summary>OverlayManager shows the window while this is true (and the overlay is visible).</summary>
    [ObservableProperty] public partial bool IsOpen { get; private set; }

    public abstract string Title { get; }

    /// <summary>Window size in DIPs.</summary>
    public virtual double Width => 720;
    public virtual double Height => 520;

    /// <summary>Closed when the player enters a match (a big card in the middle would cover the game).</summary>
    public virtual bool CloseInMatch => true;

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    public void Open()
    {
        if (IsOpen)
            return;
        IsOpen = true;
        OnOpened();
    }

    [RelayCommand]
    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        OnClosed();
    }

    /// <summary>Called right after opening: load the data here.</summary>
    protected virtual void OnOpened() { }

    /// <summary>Called right after closing: cancel pending work here.</summary>
    protected virtual void OnClosed() { }
}
