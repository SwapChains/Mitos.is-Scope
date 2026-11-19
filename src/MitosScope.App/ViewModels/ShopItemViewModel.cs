using System.Collections.Generic;
using Avalonia.Media;
using MitosScope.App.Services;
using MitosScope.Core.Api;
using MitosScope.Core.Items;

namespace MitosScope.App.ViewModels;

/// <summary>One item for sale, as shown in the shop window (immutable; rebuilt on each load).</summary>
public sealed class ShopItemViewModel(ShopItem item, long coins, long? playerLevel, string? categoryName = null)
{
    public ShopItem Item { get; } = item;
    public string Name { get; } = EquippedItemViewModel.CleanName(item.Name);
    public IImage? Sprite { get; } = ItemSprites.Get(item.Sprite);

    /// <summary>The shop category, shown in the search results (they mix every category).</summary>
    public string? CategoryName { get; } = categoryName;
    public bool HasCategoryName => !string.IsNullOrEmpty(CategoryName);

    /// <summary>The description without the "+12 Speed" lines (the attributes are listed on their own).</summary>
    public string? Description => ItemText.Description(Item.Description, Item.Attrs.Count > 0);
    public bool HasDescription => Description is not null;

    public IReadOnlyList<ItemAttr> Attrs => Item.Attrs;
    public bool HasAttrs => Item.Attrs.Count > 0;

    /// <summary>"+12 Speed Â· +8 Resilience" on the tile.</summary>
    public string AttrsShortText => AttrText.Short(Item.Attrs);

    public bool Matches(string? query) => ItemText.Matches(query, Name, Item.Description, Item.Attrs);

    /// <summary>"300 coins", with "Â· x5" for packs of consumables.</summary>
    public string PriceText => Item.Pack > 1 ? $"{Item.Price:N0} coins Â· x{Item.Pack:N0}" : $"{Item.Price:N0} coins";

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

    /// <summary>Consumables (potions, powers) can be bought several packs at once (the qty of /v1/shop/buy).</summary>
    public bool IsConsumable => Item.Type == "consumable" || Item.Pack > 1;

    /// <summary>
    /// Whether the Buy button can be pressed. can_buy is false once a consumable is owned, but more packs
    /// of it can still be bought: then only the level is checked here (the server checks everything).
    /// </summary>
    public bool IsBuyable => Item.CanBuy || (IsConsumable && Item.Owned && !(playerLevel is { } lvl && Item.Level > lvl));

    /// <summary>"Requires level 20", "Owned", â€¦: why the card has no Buy button, or "" when it has one.</summary>
    public string NotBuyableText => IsBuyable ? "" : StateText.Length > 0 ? StateText : "Can't be bought";

    /// <summary>"Level 12" on the card.</summary>
    public string LevelText => Item.Level > 0 ? $"Level {Item.Level:N0}" : "";
    public bool HasLevelText => LevelText.Length > 0;
}

/// <summary>Short attribute lines for the tiles.</summary>
public static class AttrText
{
    public static string Short(IReadOnlyList<ItemAttr> attrs, int max = 2)
    {
        if (attrs.Count == 0)
            return "";
        var parts = new List<string>();
        for (var i = 0; i < attrs.Count && i < max; i++)
            parts.Add($"{attrs[i].ValueText} {attrs[i].DisplayName}");
        if (attrs.Count > max)
            parts.Add($"+{attrs.Count - max} more");
        return string.Join(" Â· ", parts);
    }

    /// <summary>One attribute per line, for tooltips.</summary>
    public static string Lines(IReadOnlyList<ItemAttr> attrs) =>
        attrs.Count == 0 ? "No attributes" : string.Join("\n", System.Linq.Enumerable.Select(attrs, a => $"{a.ValueText}  {a.DisplayName}"));
}
