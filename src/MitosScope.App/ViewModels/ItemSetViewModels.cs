using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Services;
using MitosScope.Core.Api;
using MitosScope.Core.Items;

namespace MitosScope.App.ViewModels;

/// <summary>One item of a set as shown in the list: the owned item when it's still there, else the copy saved in the set.</summary>
public sealed class ItemSetItemViewModel(ItemSetEntry entry, InventoryItemViewModel? owned, bool equipped)
{
    public ItemSetEntry Entry { get; } = entry;
    public InventoryItemViewModel? Owned { get; } = owned;
    public string Name { get; } = owned?.Name ?? EquippedItemViewModel.CleanName(entry.Name);
    public IImage? Sprite { get; } = ItemSprites.Get(owned?.Item.Sprite ?? entry.Sprite);
    public IReadOnlyList<ItemAttr> Attrs { get; } = owned?.Item.Attrs ?? entry.Attrs;

    /// <summary>No longer in the inventory (sold, expired, other account): it's skipped when equipping.</summary>
    public bool IsMissing => Owned is null;
    public bool IsEquipped { get; } = equipped;

    public string StateText => IsMissing ? "Not owned" : IsEquipped ? "Equipped" : "";
    public bool HasStateText => StateText.Length > 0;
    public string Tip => $"{Name}\n{AttrText.Lines(Attrs)}" + (IsMissing ? "\nNot in your inventory: buy it in the shop" : "");
}

/// <summary>One item set in the inventory window: its items, what they give together and the actions.</summary>
public sealed partial class ItemSetViewModel : ObservableObject
{
    public ItemSetViewModel(ItemSet set, IReadOnlyDictionary<long, InventoryItemViewModel>? owned, IReadOnlySet<long> equipped, int index, int count)
    {
        Set = set;
        Items = set.Items.Select(e => new ItemSetItemViewModel(e, owned?.GetValueOrDefault(e.Id), equipped.Contains(e.Id))).ToList();
        Totals = ItemSets.SumAttrs(Items.Select(i => i.Attrs)).Select(a => new AttrLineViewModel(a, false)).ToList();
        IsActive = Items.Count > 0 && Items.All(i => i.IsEquipped);
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
    }

    public ItemSet Set { get; }
    public string Name => Set.Name;
    public IReadOnlyList<ItemSetItemViewModel> Items { get; }
    public IReadOnlyList<AttrLineViewModel> Totals { get; }
    public bool HasTotals => Totals.Count > 0;

    /// <summary>Every item of the set is equipped now.</summary>
    public bool IsActive { get; }
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }

    public int MissingCount => Items.Count(i => i.IsMissing);
    public string SummaryText => MissingCount > 0
        ? $"{Items.Count} item{(Items.Count == 1 ? "" : "s")} · {MissingCount} not owned"
        : $"{Items.Count} item{(Items.Count == 1 ? "" : "s")}";

    /// <summary>Points the set gives in total (all attributes added up).</summary>
    public string PointsText => Totals.Count == 0 ? "No attributes" : $"{Totals.Sum(t => t.Attr.Value):+0.##;-0.##;0} points";

    [ObservableProperty] public partial bool IsRenaming { get; set; }
    [ObservableProperty] public partial string? RenameText { get; set; }

    [ObservableProperty] public partial bool IsConfirmingDelete { get; set; }

    /// <summary>"Equipping 2/3…", "Equipped", errors: shown on the Equip button for a while.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EquipText))]
    public partial string? EquipStatus { get; set; }

    public string EquipText => EquipStatus ?? (IsActive ? "Equipped" : "Equip");
}

/// <summary>One slot of the set editor: the item picked for it, or empty.</summary>
public sealed partial class SetSlotViewModel(int index) : ObservableObject
{
    public int Index { get; } = index;
    public string EmptyText => $"Slot {Index + 1}: empty";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItem), nameof(Name), nameof(Sprite), nameof(AttrsText))]
    public partial ItemSetItemViewModel? Item { get; set; }

    public bool HasItem => Item is not null;
    public string Name => Item?.Name ?? EmptyText;
    public IImage? Sprite => Item?.Sprite;
    public string AttrsText => Item is { } i ? AttrText.Short(i.Attrs, 3) : "Pick an item below";
}
