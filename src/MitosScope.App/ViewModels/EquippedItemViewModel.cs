using System;
using System.Text.RegularExpressions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Services;
using MitosScope.Core.Api;

namespace MitosScope.App.ViewModels;

/// <summary>One equipped item as shown in the Equipment panel (rebuilt on each equipment poll).</summary>
public sealed partial class EquippedItemViewModel(EquipmentSlot item, SessionViewModel owner) : ObservableObject
{
    public EquipmentSlot Item { get; } = item;

    /// <summary>The session it belongs to: its RepairCommand repairs this item (CommandParameter).</summary>
    public SessionViewModel Owner { get; } = owner;
    public string Name { get; } = CleanName(item.Name);

    /// <summary>Drops rarity tags like "* SPECIAL *", "* LIMITED *", "* SUPER LIMITED *".</summary>
    internal static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";
        var clean = RarityTag().Replace(name, "").Trim();
        return clean.Length > 0 ? clean : name.Trim();
    }

    [GeneratedRegex(@"\s*\*[^*]*\*")]
    private static partial Regex RarityTag();
    public IImage? Sprite { get; } = ItemSprites.Get(item.Sprite);
    public int Slot => Item.Slot;

    /// <summary>
    /// For consumables the API's "durability" looks like the quantity owned (e.g. 11252/3), so
    /// they get a quantity instead of a durability bar.
    /// </summary>
    public bool IsConsumable { get; } = string.Equals(item.Type, "consumable", StringComparison.OrdinalIgnoreCase);
    public bool HasDurability => !IsConsumable && Item.MaxDurability > 0;

    public double DurabilityFraction => HasDurability ? Math.Clamp((double)Item.Durability / Item.MaxDurability, 0, 1) : 0;

    public string DetailText => IsConsumable
        ? $"x{Item.Durability:N0}"
        : HasDurability ? $"{Item.Durability}/{Item.MaxDurability}" : "";

    public bool IsLow => HasDurability && DurabilityFraction <= 0.25;
    public bool IsMedium => HasDurability && DurabilityFraction is > 0.25 and <= 0.5;

    /// <summary>repair_price (API v3) is 0 when the item isn't damaged or can't be repaired.</summary>
    public bool CanRepair => Item.RepairPrice > 0;
    public string RepairText => RepairStatus ?? (CanRepair ? $"Repair: {Item.RepairPrice:N0} coins" : "");

    /// <summary>"Unequipping…" or why it failed, under the name for a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionStatus))]
    public partial string? ActionStatus { get; set; }

    public bool HasActionStatus => ActionStatus is not null;

    /// <summary>An unequip of this item is running.</summary>
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>Shown on the repair button instead of the price for a few seconds ("Repairing…", errors).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairText))]
    public partial string? RepairStatus { get; set; }
}
