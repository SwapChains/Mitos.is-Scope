using Avalonia.Controls;
using MitosScope.App.ViewModels;

namespace MitosScope.App.Overlay;

/// <summary>The window of one popup (see PopupViewModel). OverlayManager shows, hides and centers it.</summary>
public partial class PopupWindow : Window
{
    public PopupWindow()
    {
        InitializeComponent();
    }

    public PopupWindow(PopupViewModel popup) : this()
    {
        DataContext = popup;
    }

    public PopupViewModel Popup => (PopupViewModel)DataContext!;
}
