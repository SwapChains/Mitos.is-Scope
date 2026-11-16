using Avalonia.Controls;
using Avalonia.Input;
using MitosScope.App.ViewModels;

namespace MitosScope.App.Views;

public partial class GemTrainingView : UserControl
{
    public GemTrainingView() => InitializeComponent();

    /// <summary>A click on the dark area around the gem card (not on the card itself) closes it.</summary>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source == sender && DataContext is GemTrainingViewModel vm)
            vm.CloseGemCommand.Execute(null);
    }
}
