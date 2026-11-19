using MitosScope.Core.Api;

namespace MitosScope.Core.Items;

/// <summary>An item the optimizer can pick: from the shop or already owned (then it costs nothing).</summary>
public sealed record BuildCandidate(
    long Id,
    string Name,
    string? Sprite,
    int Category,
    long Price,
    long Level,
    bool Owned,
    IReadOnlyList<ItemAttr> Attrs);

/// <summary>One combination found: up to 3 items, one per category, like the game's equipment slots 0-2.</summary>
public sealed record BuildResult(IReadOnlyList<BuildCandidate> Items, double Score, long Cost)
{
    private IReadOnlyList<ItemAttr>? _totals;

    /// <summary>Every attribute of the combination added up, highest first.</summary>
    public IReadOnlyList<ItemAttr> Totals => _totals ??= ItemSets.SumAttrs(Items.Select(i => i.Attrs));
}

public sealed record BuildOptions
{
    /// <summary>How many items the build has (the game has 3 equipment slots).</summary>
    public int Slots { get; init; } = 3;

    /// <summary>Only items the player's level allows (null = any level).</summary>
    public long? MaxLevel { get; init; }

    /// <summary>Most coins the items still to buy may cost together (null = no limit).</summary>
    public long? Budget { get; init; }

    /// <summary>How many builds to return, best first.</summary>
    public int Results { get; init; } = 5;
}

/// <summary>
/// Finds the items that give the most points in the chosen attributes: one item per category (the
/// game equips one per category in slots 0-2), every chosen attribute weighted the same. With equal
/// points the cheaper build wins (owned items cost 0), then the one with fewer negative attributes.
/// </summary>
public static class BuildOptimizer
{
    /// <summary>Best items kept per category without a budget (only the top ones can be in the best builds).</summary>
    private const int PerCategory = 12;

    public static double Score(IReadOnlyList<ItemAttr> attrs, IReadOnlySet<string> wanted)
    {
        double score = 0;
        foreach (var a in attrs)
            if (wanted.Contains(a.Name))
                score += a.Value;
        return score;
    }

    public static IReadOnlyList<BuildResult> Find(IEnumerable<BuildCandidate> candidates, IReadOnlySet<string> wanted, BuildOptions options)
    {
        if (wanted.Count == 0)
            return [];

        // One entry per item id: owned wins over the shop copy (free).
        var items = candidates
            .Where(c => options.MaxLevel is not { } max || c.Owned || c.Level <= max)
            .GroupBy(c => c.Id)
            .Select(g => g.OrderByDescending(c => c.Owned).First())
            .Select(c => (Item: c, Score: Score(c.Attrs, wanted), Cost: c.Owned ? 0 : c.Price))
            .Where(x => x.Score > 0)
            .ToList();

        // Per category, the items that can be part of a best build: the top ones by points, and with a
        // budget also the cheaper ones that beat every cheaper item (a tight budget may need them).
        var groups = items
            .GroupBy(x => x.Item.Category)
            .Select(g =>
            {
                var top = g.OrderByDescending(x => x.Score).ThenBy(x => x.Cost).Take(PerCategory);
                if (options.Budget is null)
                    return top.ToList();
                var frontier = new List<(BuildCandidate Item, double Score, long Cost)>();
                var best = double.MinValue;
                foreach (var x in g.OrderBy(x => x.Cost).ThenByDescending(x => x.Score))
                {
                    if (x.Score <= best)
                        continue;
                    best = x.Score;
                    frontier.Add(x);
                }
                return top.Concat(frontier).Distinct().ToList();
            })
            .ToList();

        var results = new List<BuildResult>();
        var picked = new List<(BuildCandidate Item, double Score, long Cost)>();
        var slots = Math.Clamp(options.Slots, 1, 3);

        void Walk(int from)
        {
            if (picked.Count > 0)
            {
                var cost = picked.Sum(p => p.Cost);
                if (options.Budget is { } budget && cost > budget)
                    return;
                // fewer items too: with a tight budget or few categories they can be the best there is
                results.Add(new BuildResult(picked.Select(p => p.Item).ToList(), picked.Sum(p => p.Score), cost));
            }
            if (picked.Count == slots)
                return;
            for (var g = from; g < groups.Count; g++)
            {
                foreach (var x in groups[g])
                {
                    picked.Add(x);
                    Walk(g + 1);
                    picked.RemoveAt(picked.Count - 1);
                }
            }
        }

        Walk(0);

        return results
            .OrderByDescending(r => Math.Round(r.Score, 4))
            .ThenBy(r => r.Cost)
            .ThenBy(r => r.Items.Sum(i => i.Attrs.Count(a => a.Value < 0)))
            .ThenBy(r => r.Items.Count)
            .DistinctBy(r => string.Join(",", r.Items.Select(i => i.Id).Order()))
            .Take(Math.Max(1, options.Results))
            .ToList();
    }
}
