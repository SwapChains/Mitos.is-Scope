using Avalonia.Controls;
using Avalonia.Input;
using MitosScope.App.ViewModels;

namespace MitosScope.App.Views;

public partial class InventoryView : UserControl
{
    public InventoryView() => InitializeComponent();

    /// <summary>A click on the dark area around the item card (not on the card itself) closes it.</summary>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is InventoryViewModel vm)
            vm.CloseItemCommand.Execute(null);
    }
}
