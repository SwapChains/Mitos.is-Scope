using Avalonia.Controls;
using MitosScope.App.ViewModels;

namespace MitosScope.App.Overlay;

public partial class ShopWindow : Window
{
    public ShopWindow()
    {
        InitializeComponent();
    }

    public ShopWindow(ShopViewModel shop) : this()
    {
        DataContext = shop;
    }
}
