using MitosScope.Core.Api;

namespace MitosScope.Core.Items;

/// <summary>
/// An item set made in MitosScope (not the game's equipment sets): a name and up to 3 equipment items,
/// one per category, for the equipment slots 0-2. Kept in settings.json per account. The item's name,
/// icon and attributes are copied when it's added, so the set still shows them if the item is gone.
/// </summary>
public sealed class ItemSet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<ItemSetEntry> Items { get; set; } = [];
    public long Updated { get; set; }
}

public sealed class ItemSetEntry
{
    public long Id { get; set; }
    public string? Name { get; set; }
    public string? Sprite { get; set; }
    public int Category { get; set; }
    public List<ItemAttr> Attrs { get; set; } = [];
}

/// <summary>One POST /v1/equip to put a set on: the item and the slot it goes in.</summary>
public sealed record EquipStep(long ItemId, int Slot);

public static class ItemSets
{
    /// <summary>Equipment slots a set fills (the game's slots 0-2; 3-4 are potions, 5 the gem).</summary>
    public const int Slots = 3;

    public const int NameMax = 24;

    /// <summary>Attributes added up by id, highest first.</summary>
    public static IReadOnlyList<ItemAttr> SumAttrs(IEnumerable<IEnumerable<ItemAttr>> lists)
    {
        var totals = new Dictionary<string, ItemAttr>();
        foreach (var list in lists)
            foreach (var a in list)
                totals[a.Name] = totals.TryGetValue(a.Name, out var t)
                    ? t with { Value = t.Value + a.Value, Label = t.Label ?? a.Label }
                    : a;
        return totals.Values
            .Where(a => Math.Abs(a.Value) > 0.0001)
            .OrderByDescending(a => a.Value)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Which slot each item of the set goes in, given what's equipped now (slot → item id and category).
    /// Items already in slots 0-2 stay where they are. An item takes the slot of the equipped item of its
    /// category (the game allows one per category), else a free slot, else a slot whose item isn't in the
    /// set. The slot must be explicit: without it the addon API would put every item in slot 0.
    /// </summary>
    public static IReadOnlyList<EquipStep> Plan(ItemSet set, IReadOnlyList<(int Slot, long Id, int Category)> equipped)
    {
        var current = equipped.Where(e => e.Slot is >= 0 and < Slots).ToList();
        var setIds = set.Items.Select(i => i.Id).ToHashSet();
        var taken = new HashSet<int>(current.Where(e => setIds.Contains(e.Id)).Select(e => e.Slot));
        var steps = new List<EquipStep>();
        var waiting = new List<ItemSetEntry>();

        foreach (var item in set.Items.Take(Slots))
        {
            if (current.Any(e => e.Id == item.Id))
                continue;
            var same = current.FirstOrDefault(e => e.Category == item.Category && !taken.Contains(e.Slot));
            if (current.Any(e => e.Category == item.Category && !taken.Contains(e.Slot)))
            {
                taken.Add(same.Slot);
                steps.Add(new EquipStep(item.Id, same.Slot));
            }
            else
                waiting.Add(item);
        }

        foreach (var item in waiting)
        {
            var free = Enumerable.Range(0, Slots).Where(s => !taken.Contains(s)).ToList();
            if (free.Count == 0)
                break;
            var slot = free.FirstOrDefault(s => current.All(e => e.Slot != s), free[0]);
            taken.Add(slot);
            steps.Add(new EquipStep(item.Id, slot));
        }

        return steps;
    }
}
