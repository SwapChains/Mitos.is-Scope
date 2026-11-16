using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Settings;

namespace MitosScope.App.ViewModels.Panels;

/// <summary>
/// Base for every overlay panel. Each panel gets its own window (see Overlay/PanelWindow);
/// its view is resolved by ViewLocator (XxxPanelViewModel → Views/Panels/XxxPanelView).
/// To add a panel: create the ViewModel + View and register it in App.CreatePanels().
/// </summary>
public abstract partial class PanelViewModel(TrackerViewModel tracker) : ViewModelBase
{
    public TrackerViewModel Tracker { get; } = tracker;

    /// <summary>Stable key used to persist the layout.</summary>
    public abstract string Id { get; }

    public abstract string Title { get; }

    public abstract PanelLayout DefaultLayout { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClickable))]
    public partial bool IsEditMode { get; set; }

    /// <summary>
    /// Takes clicks outside edit mode (instead of letting them through to the game); a click calls
    /// <see cref="OnClicked"/>. Still never takes the focus from the game. To open a big card in
    /// the middle of the screen from the click, call Toggle() on a PopupViewModel (see ShopPanelViewModel).
    /// </summary>
    public virtual bool IsInteractive => false;

    /// <summary>Clickable right now: in edit mode a click drags the panel instead.</summary>
    public bool IsClickable => IsInteractive && !IsEditMode;

    public virtual void OnClicked() { }
}

/// <summary>Small clickable card that opens the shop window (see ShopViewModel).</summary>
public sealed class ShopPanelViewModel(TrackerViewModel tracker, ShopViewModel shop) : PanelViewModel(tracker)
{
    public ShopViewModel Shop { get; } = shop;
    public override string Id => "shop";
    public override string Title => "Shop";
    // Menu only by default: in a match a clickable card would catch clicks meant for the game.
    public override PanelLayout DefaultLayout => new() { X = 600, Y = 280, Width = 200, Height = 70, ShowInMatch = false };
    public override bool IsInteractive => true;
    public override void OnClicked() => Shop.Toggle();
}

/// <summary>Small clickable card that opens the inventory window (see InventoryViewModel).</summary>
public sealed class InventoryPanelViewModel(TrackerViewModel tracker, InventoryViewModel inventory) : PanelViewModel(tracker)
{
    public InventoryViewModel Inventory { get; } = inventory;
    public override string Id => "inventory";
    public override string Title => "Inventory";
    // Menu only by default, like the Shop card (just below it).
    public override PanelLayout DefaultLayout => new() { X = 600, Y = 360, Width = 200, Height = 70, ShowInMatch = false };
    public override bool IsInteractive => true;
    public override void OnClicked() => Inventory.Toggle();
}

/// <summary>The 3 gem training slots, compact; a click opens the gem training window (see GemTrainingViewModel).</summary>
public sealed class GemTrainingPanelViewModel(TrackerViewModel tracker, GemTrainingViewModel gems) : PanelViewModel(tracker)
{
    public GemTrainingViewModel Gems { get; } = gems;
    public override string Id => "gemtraining";
    public override string Title => "Gem Training";
    // Menu only by default, like the Shop and Inventory cards (it's clickable too).
    public override PanelLayout DefaultLayout => new() { X = 600, Y = 440, Width = 240, Height = 150, ShowInMatch = false };
    public override bool IsInteractive => true;
    public override void OnClicked() => Gems.Toggle();
}

public sealed class StatusPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "status";
    public override string Title => "MitosScope";
    public override PanelLayout DefaultLayout => new() { X = 20, Y = 80, Width = 260, Height = 150 };
}

public sealed class GamePanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "game";
    public override string Title => "Match";
    public override PanelLayout DefaultLayout => new() { X = 20, Y = 240, Width = 260, Height = 285 };
}

public sealed class AccountPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "account";
    public override string Title => "Account";
    public override PanelLayout DefaultLayout => new() { X = 20, Y = 450, Width = 260, Height = 205 };
}

public sealed class QuestsPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "quests";
    public override string Title => "Quests";
    public override PanelLayout DefaultLayout => new() { X = 290, Y = 80, Width = 300, Height = 220 };
}

public sealed class ProfilePanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "profile"; // kept as "profile" so the saved layout still applies
    public override string Title => "All Time Stats";
    public override PanelLayout DefaultLayout => new() { X = 600, Y = 80, Width = 260, Height = 190 };
}

public sealed class BattlePassPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "battlepass";
    public override string Title => "Battle Pass";
    public override PanelLayout DefaultLayout => new() { X = 870, Y = 80, Width = 260, Height = 165 };
}

public sealed class EquipmentPanelViewModel(TrackerViewModel tracker) : PanelViewModel(tracker)
{
    public override string Id => "equipment";
    public override string Title => "Equipment";
    public override PanelLayout DefaultLayout => new() { X = 290, Y = 310, Width = 300, Height = 310 };
}
