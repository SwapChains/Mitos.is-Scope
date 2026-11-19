using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Services;
using MitosScope.Core.Api;
using MitosScope.Core.Items;

namespace MitosScope.App.ViewModels;

/// <summary>
/// An attribute the player can pick in the shop build tool. <see cref="Priority"/> is the order it was
/// clicked in (1 = maximized first), 0 when not picked.
/// </summary>
public sealed partial class BuildAttrViewModel(string name, string label) : ObservableObject
{
    public string Name { get; } = name;
    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelected), nameof(PriorityText), nameof(Tip))]
    public partial int Priority { get; set; }

    public bool IsSelected => Priority > 0;

    /// <summary>"1°", "2°"…</summary>
    public string PriorityText => Priority > 0 ? $"{Priority}°" : "";

    public string Tip => Priority switch
    {
        0 => "Click to maximize it (the order you click sets the priority)",
        1 => "Maximized first. Click to remove it",
        _ => $"Priority {Priority}: decides only between builds equal in the attributes before it. Click to remove it",
    };
}

/// <summary>One attribute line of a build or a set: "+30 Speed", highlighted when it's one the player picked.</summary>
public sealed class AttrLineViewModel(ItemAttr attr, bool wanted, int priority = 0)
{
    public ItemAttr Attr { get; } = attr;
    public string ValueText => Attr.ValueText;
    public string Name => Attr.DisplayName;
    public bool IsWanted { get; } = wanted;

    /// <summary>"1°" before the picked attributes of a build.</summary>
    public string PriorityText { get; } = priority > 0 ? $"{priority}°" : "";
    public bool HasPriority => priority > 0;
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
    public BuildResultViewModel(int rank, BuildResult result, IReadOnlyList<BuildAttrViewModel> wanted, IReadOnlyDictionary<long, ShopItemViewModel> shop)
    {
        Result = result;
        Rank = rank;
        Picks = result.Items.Select(i => new BuildPickViewModel(i, shop.GetValueOrDefault(i.Id))).ToList();
        // the picked attributes in priority order, with their total (also when the build gives 0 of one)
        Wanted = wanted.Select((w, i) => new AttrLineViewModel(
                new ItemAttr { Name = w.Name, Label = w.Label, Value = result.Scores[i] }, true, i + 1))
            .ToList();
        var names = wanted.Select(w => w.Name).ToHashSet();
        Others = result.Totals.Where(a => !names.Contains(a.Name)).Select(a => new AttrLineViewModel(a, false)).ToList();
    }

    public BuildResult Result { get; }
    public int Rank { get; }
    public string RankText => $"#{Rank}";
    public bool IsBest => Rank == 1;
    public IReadOnlyList<BuildPickViewModel> Picks { get; }
    public IReadOnlyList<AttrLineViewModel> Wanted { get; }
    public IReadOnlyList<AttrLineViewModel> Others { get; }
    public bool HasOthers => Others.Count > 0;

    /// <summary>The total of the 1st attribute, the one that ranks the builds.</summary>
    public string ScoreText => Wanted.Count > 0 ? $"{Wanted[0].ValueText} {Wanted[0].Name}" : "";

    public string CostText => Result.Cost > 0
        ? $"To buy: {Result.Cost:N0} coins ({Picks.Count(p => !p.IsOwned)} item{(Picks.Count(p => !p.IsOwned) == 1 ? "" : "s")})"
        : "You own all of them";
}
