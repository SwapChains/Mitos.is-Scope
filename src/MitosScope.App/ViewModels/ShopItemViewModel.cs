using Avalonia.Media;
using MitosScope.App.Services;
using MitosScope.Core.Api;

namespace MitosScope.App.ViewModels;

/// <summary>One item for sale, as shown in the shop window (immutable; rebuilt on each load).</summary>
public sealed class ShopItemViewModel(ShopItem item, long coins, long? playerLevel)
{
    public ShopItem Item { get; } = item;
    public string Name { get; } = EquippedItemViewModel.CleanName(item.Name);
    public IImage? Sprite { get; } = ItemSprites.Get(item.Sprite);

    public string? Description => string.IsNullOrWhiteSpace(Item.Description) ? null : Item.Description;

    /// <summary>"300 coins", with "· x5" for packs of consumables.</summary>
    public string PriceText => Item.Pack > 1 ? $"{Item.Price:N0} coins · x{Item.Pack:N0}" : $"{Item.Price:N0} coins";

    public bool IsOwned => Item.Owned;

    /// <summary>Not owned and can't be bought: dimmed, with the most likely reason.</summary>
    public bool IsLocked => !Item.Owned && !Item.CanBuy;

    public string StateText => Item switch
    {
        { Owned: true } => "Owned",
        { CanBuy: true } => "",
        // can_buy needs the level, the achievement and the coins: show the reason we can tell.
        _ when playerLevel is { } lvl && Item.Level > lvl => $"Requires level {Item.Level:N0}",
        _ when Item.Price > coins => "Not enough coins",
        _ => "Locked",
    };

    public bool HasStateText => StateText.Length > 0;
}
