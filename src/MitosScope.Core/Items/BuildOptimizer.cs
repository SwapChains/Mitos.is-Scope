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

/// <summary>
/// One combination found: up to 3 items, one per category, like the game's equipment slots 0-2.
/// <see cref="Scores"/> has the points in each chosen attribute, in priority order.
/// </summary>
public sealed record BuildResult(IReadOnlyList<BuildCandidate> Items, IReadOnlyList<double> Scores, long Cost)
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
/// Finds the items that give the most points in the chosen attributes, one item per category (the
/// game equips one per category in slots 0-2). The attributes are in priority order: the build with
/// the most points in the 1st wins; the 2nd only decides between builds equal in the 1st, and so on.
/// Then the cheaper build wins (owned items cost 0), then the one with fewer negative attributes.
/// </summary>
public static class BuildOptimizer
{
    /// <summary>Best items kept per category without a budget (only the top ones can be in the best builds).</summary>
    private const int PerCategory = 12;

    private const double Eps = 0.0001;

    /// <summary>Points in each wanted attribute, in the order of <paramref name="wanted"/>.</summary>
    public static double[] Scores(IReadOnlyList<ItemAttr> attrs, IReadOnlyList<string> wanted)
    {
        var scores = new double[wanted.Count];
        foreach (var a in attrs)
            for (var i = 0; i < wanted.Count; i++)
                if (wanted[i] == a.Name)
                    scores[i] += a.Value;
        return scores;
    }

    /// <summary>Priority order: the first attribute that differs decides.</summary>
    public static int Compare(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        for (var i = 0; i < a.Count && i < b.Count; i++)
        {
            var d = a[i] - b[i];
            if (d > Eps)
                return 1;
            if (d < -Eps)
                return -1;
        }
        return 0;
    }

    public static IReadOnlyList<BuildResult> Find(IEnumerable<BuildCandidate> candidates, IReadOnlyList<string> wanted, BuildOptions options)
    {
        if (wanted.Count == 0)
            return [];

        var zero = new double[wanted.Count];

        // One entry per item id: owned wins over the shop copy (free). Only items that help: better than nothing.
        var items = candidates
            .Where(c => options.MaxLevel is not { } max || c.Owned || c.Level <= max)
            .GroupBy(c => c.Id)
            .Select(g => g.OrderByDescending(c => c.Owned).First())
            .Select(c => new Pick(c, Scores(c.Attrs, wanted), c.Owned ? 0 : c.Price))
            .Where(x => Compare(x.Scores, zero) > 0)
            .ToList();

        // Per category, the items that can be part of a best build: the top ones, and with a budget also
        // the cheaper ones that beat every cheaper item (a tight budget may need them).
        var groups = items
            .GroupBy(x => x.Item.Category)
            .Select(g =>
            {
                var sorted = g.ToList();
                sorted.Sort((a, b) => Compare(b.Scores, a.Scores) is var c && c != 0 ? c : a.Cost.CompareTo(b.Cost));
                var keep = sorted.Take(PerCategory).ToList();
                if (options.Budget is null)
                    return keep;
                double[]? best = null;
                foreach (var x in g.OrderBy(x => x.Cost))
                {
                    if (best is not null && Compare(x.Scores, best) <= 0)
                        continue;
                    best = x.Scores;
                    if (!keep.Contains(x))
                        keep.Add(x);
                }
                return keep;
            })
            .ToList();

        var results = new List<BuildResult>();
        var picked = new List<Pick>();
        var slots = Math.Clamp(options.Slots, 1, 3);

        void Walk(int from)
        {
            if (picked.Count > 0)
            {
                var cost = picked.Sum(p => p.Cost);
                if (options.Budget is { } budget && cost > budget)
                    return;
                // fewer items too: with a tight budget or few categories they can be the best there is
                var scores = new double[wanted.Count];
                foreach (var p in picked)
                    for (var i = 0; i < scores.Length; i++)
                        scores[i] += p.Scores[i];
                results.Add(new BuildResult(picked.Select(p => p.Item).ToList(), scores, cost));
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

        results.Sort((a, b) =>
        {
            var c = Compare(b.Scores, a.Scores);
            if (c != 0) return c;
            c = a.Cost.CompareTo(b.Cost);
            if (c != 0) return c;
            c = Negatives(a).CompareTo(Negatives(b));
            return c != 0 ? c : a.Items.Count.CompareTo(b.Items.Count);
        });

        return results
            .DistinctBy(r => string.Join(",", r.Items.Select(i => i.Id).Order()))
            .Take(Math.Max(1, options.Results))
            .ToList();
    }

    private static int Negatives(BuildResult r) => r.Items.Sum(i => i.Attrs.Count(a => a.Value < 0));

    private sealed record Pick(BuildCandidate Item, double[] Scores, long Cost);
}
