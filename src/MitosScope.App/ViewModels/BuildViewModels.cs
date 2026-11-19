using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Services;
using MitosScope.Core.Api;
using MitosScope.Core.Items;

namespace MitosScope.App.ViewModels;

/// <summary>An attribute the player can pick in the shop build tool.</summary>
public sealed partial class BuildAttrViewModel(string name, string label, bool selected) : ObservableObject
{
    public string Name { get; } = name;
    public string Label { get; } = label;

    [ObservableProperty] public partial bool IsSelected { get; set; } = selected;
}

/// <summary>One attribute line of a build or a set: "+30 Speed", highlighted when it's one the player picked.</summary>
public sealed class AttrLineViewModel(ItemAttr attr, bool wanted)
{
    public ItemAttr Attr { get; } = attr;
    public string ValueText => Attr.ValueText;
    public string Name => Attr.DisplayName;
    public bool IsWanted { get; } = wanted;
    public bool IsNegative => Attr.IsNegative;
    public bool IsPlain => !IsWanted && !IsNegative;
}

/// <summary>One item of a build: owned (free) or from the shop (a click opens its Buy card).</summary>
public sealed class BuildPickViewModel(BuildCandidate item, ShopItemViewModel? shopItem)
{
    public BuildCandidate Item { get; } = item;
    public ShopItemViewModel? ShopItem { get; } = shopItem;
    public string Name => Item.Name;
    public IImage? Sprite { get; } = ItemSprites.Get(item.Sprite);
    public bool IsOwned => Item.Owned;
    public string PriceText => Item.Owned ? "Owned" : $"{Item.Price:N0} coins";
    public string AttrsText { get; } = AttrText.Short(item.Attrs, 3);
    public string Tip => $"{Item.Name}\n{AttrText.Lines(Item.Attrs)}" + (Item.Owned ? "\nAlready in your inventory" : "\nClick to buy it");
}

/// <summary>One combination found by the build tool.</summary>
public sealed class BuildResultViewModel
{
    public BuildResultViewModel(int rank, BuildResult result, IReadOnlySet<string> wanted, IReadOnlyDictionary<long, ShopItemViewModel> shop)
    {
        Result = result;
        Rank = rank;
        Picks = result.Items.Select(i => new BuildPickViewModel(i, shop.GetValueOrDefault(i.Id))).ToList();
        Wanted = result.Totals.Where(a => wanted.Contains(a.Name)).Select(a => new AttrLineViewModel(a, true)).ToList();
        Others = result.Totals.Where(a => !wanted.Contains(a.Name)).Select(a => new AttrLineViewModel(a, false)).ToList();
    }

    public BuildResult Result { get; }
    public int Rank { get; }
    public string RankText => $"#{Rank}";
    public bool IsBest => Rank == 1;
    public IReadOnlyList<BuildPickViewModel> Picks { get; }
    public IReadOnlyList<AttrLineViewModel> Wanted { get; }
    public IReadOnlyList<AttrLineViewModel> Others { get; }
    public bool HasOthers => Others.Count > 0;

    public string ScoreText => $"+{Result.Score:0.##} points";

    public string CostText => Result.Cost > 0
        ? $"To buy: {Result.Cost:N0} coins ({Picks.Count(p => !p.IsOwned)} item{(Picks.Count(p => !p.IsOwned) == 1 ? "" : "s")})"
        : "You own all of them";
}
