using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Services;
using MitosScope.Core.Api;
using MitosScope.Core.Items;

namespace MitosScope.App.ViewModels;

/// <summary>One owned item in the inventory window. Only the equipped state changes after it's built.</summary>
public sealed partial class InventoryItemViewModel(InventoryItem item) : ObservableObject
{
    public InventoryItem Item { get; } = item;
    public string Name { get; } = EquippedItemViewModel.CleanName(item.Name);
    public IImage? Sprite { get; } = ItemSprites.Get(item.Sprite);

    public bool IsConsumable { get; } = string.Equals(item.Type, "consumable", StringComparison.OrdinalIgnoreCase);

    /// <summary>Goes in the equipment slots 0-2, so it can be part of an item set.</summary>
    public bool IsSetEquipment => ShopViewModel.IsSlotEquipment(Item.Type, Item.Category, Item.Passive);

    /// <summary>The description without the "+12 Speed" lines (the attributes are listed on their own).</summary>
    public string? Description => ItemText.Description(Item.Description, Item.Attrs.Count > 0);
    public bool HasDescription => Description is not null;

    public IReadOnlyList<ItemAttr> Attrs => Item.Attrs;
    public bool HasAttrs => Item.Attrs.Count > 0;
    public string AttrsShortText => AttrText.Short(Item.Attrs);
    public string AttrsTip => $"{Name}\n{AttrText.Lines(Item.Attrs)}";

    public bool Matches(string? query) => ItemText.Matches(query, Name, Item.Description, Item.Attrs);

    /// <summary>"x12" for consumables (their durability is the uses left), "80/100" for equipment.</summary>
    public string DetailText => IsConsumable
        ? $"x{Item.Durability:N0}"
        : Item.MaxDurability > 0 ? $"{Item.Durability:N0}/{Item.MaxDurability:N0}" : "";

    public bool HasDetailText => DetailText.Length > 0;

    /// <summary>Longer version for the item card.</summary>
    public string CardDetailText => IsConsumable
        ? $"{Item.Durability:N0} uses left"
        : Item.MaxDurability > 0 ? $"Durability {Item.Durability:N0} / {Item.MaxDurability:N0}" : "";

    public bool HasCardDetailText => CardDetailText.Length > 0;

    public bool IsPassive => Item.Passive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EquippedText))]
    public partial bool IsEquipped { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EquippedText))]
    public partial int Slot { get; set; } = -1;

    public string EquippedText => IsEquipped ? Slot >= 0 ? $"Equipped Â· slot {Slot}" : "Equipped" : "";

    /// <summary>The entry saved in an item set (name, icon and attributes copied, in case the item goes away).</summary>
    public ItemSetEntry ToSetEntry() => new()
    {
        Id = Item.Id,
        Name = Name,
        Sprite = Item.Sprite,
        Category = Item.Category,
        Attrs = Item.Attrs.ToList(),
    };

    /// <summary>
    /// Sets the equipped state from the equipped slots (/v1/equipment, never cached), or from the
    /// inventory's own flags when the slots aren't known.
    /// </summary>
    public void ApplyEquipped(IReadOnlyDictionary<long, int>? equippedSlots)
    {
        if (equippedSlots is null)
        {
            IsEquipped = Item.Equipped;
            Slot = Item.Slot;
        }
        else
        {
            IsEquipped = equippedSlots.TryGetValue(Item.Id, out var slot);
            Slot = IsEquipped ? slot : -1;
        }
    }
}

/// <summary>Items of one category, under a header in the inventory window. Can be collapsed (saved per category id).</summary>
public sealed partial class InventoryGroupViewModel(int category, string name, IReadOnlyList<InventoryItemViewModel> items, bool expanded)
    : ObservableObject
{
    public int Category { get; } = category;
    public string Name { get; } = name;
    public IReadOnlyList<InventoryItemViewModel> Items { get; } = items;
    public string Header => $"{Name} ({Items.Count:N0})";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArrowText), nameof(ToggleTip))]
    public partial bool IsExpanded { get; set; } = expanded;

    /// <summary>â–² open (click to close), â–¼ closed (click to open).</summary>
    public string ArrowText => IsExpanded ? "â–²" : "â–¼";
    public string ToggleTip => IsExpanded ? "Hide this category" : "Show this category";

    /// <summary>The same category with only the items matching the search (always open).</summary>
    public InventoryGroupViewModel Filter(string query) =>
        new(Category, Name, Items.Where(i => i.Matches(query)).ToList(), expanded: true);
}
