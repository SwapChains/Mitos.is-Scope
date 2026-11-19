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
/// Finds the items that give the most in all the chosen attributes together, one item per category (the
/// game equips one per category in slots 0-2), then orders those builds by the player's priority.
///
/// 1. The best builds are picked looking at every chosen attribute: first the builds that give points
///    in all of them, then by the sum of each attribute's share of its best possible total (so an
///    attribute with big numbers doesn't hide one with small numbers). Builds beaten in every
///    attribute by another one are dropped.
/// 2. Those builds are ordered by priority: most points in the 1st attribute, then the 2nd, and so on,
///    then the cheaper one (owned items cost 0).
/// </summary>
public static class BuildOptimizer
{
    /// <summary>Items kept per category (the ones not beaten in every attribute by another of the category).</summary>
    private const int PerCategory = 20;

    /// <summary>Best balanced builds looked at before keeping the final ones.</summary>
    private const int Shortlist = 200;

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

        var slots = Math.Clamp(options.Slots, 1, 3);
        var budgeted = options.Budget is not null;

        // One entry per item id: owned wins over the shop copy (free). Only items that give points in at least one.
        var items = candidates
            .Where(c => options.MaxLevel is not { } max || c.Owned || c.Level <= max)
            .GroupBy(c => c.Id)
            .Select(g => g.OrderByDescending(c => c.Owned).First())
            .Select(c => new Pick(c, Scores(c.Attrs, wanted), c.Owned ? 0 : c.Price))
            .Where(x => x.Scores.Any(s => s > Eps))
            .Where(x => options.Budget is not { } budget || x.Cost <= budget)
            .ToList();
        if (items.Count == 0)
            return [];

        // Best total each attribute can reach (the best item of the `slots` best categories): the scale
        // that makes every attribute count the same, whatever the size of its numbers.
        var norm = new double[wanted.Count];
        for (var i = 0; i < norm.Length; i++)
        {
            var a = i;
            norm[i] = Math.Max(Eps, items.GroupBy(x => x.Item.Category)
                .Select(g => g.Max(x => x.Scores[a]))
                .Where(v => v > 0)
                .OrderDescending()
                .Take(slots)
                .Sum());
        }
        double Balance(double[] s)
        {
            double b = 0;
            for (var i = 0; i < s.Length; i++)
                b += s[i] / norm[i];
            return b;
        }

        // Per category, the items not beaten in every attribute (and, with a budget, in price) by another
        // one of the same category; the most balanced ones if there are too many.
        var groups = items
            .GroupBy(x => x.Item.Category)
            .Select(g => g
                .Where(x => !g.Any(o => o != x && Dominates(o.Scores, o.Cost, x.Scores, x.Cost, budgeted)))
                .OrderByDescending(x => Balance(x.Scores))
                .ThenBy(x => x.Cost)
                .Take(PerCategory)
                .ToList())
            .ToList();

        var results = new List<BuildResult>();
        var picked = new List<Pick>();

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

        // 1. The best builds in all the attributes together: those that give points in every one first,
        //    then the most balanced; the ones beaten in everything by another build are dropped.
        int Covered(BuildResult r) => r.Scores.Count(s => s > Eps);
        // a build missing a chosen attribute is shown only when no build gives points in all of them
        var mostCovered = results.Count > 0 ? results.Max(Covered) : 0;
        var shortlist = results
            .Where(r => Covered(r) == mostCovered)
            .OrderByDescending(Covered)
            .ThenByDescending(r => Balance((double[])r.Scores))
            .ThenBy(r => r.Cost)
            .DistinctBy(r => string.Join(",", r.Items.Select(i => i.Id).Order()))
            .Take(Shortlist)
            .ToList();
        var best = shortlist
            .Where(r => !shortlist.Any(o => o != r && Dominates((double[])o.Scores, o.Cost, (double[])r.Scores, r.Cost, budgeted)))
            .Take(Math.Max(1, options.Results))
            .ToList();

        // 2. Ordered by the player's priority: the 1st attribute, then the 2nd…, then the cheaper.
        best.Sort((a, b) =>
        {
            var c = Covered(b).CompareTo(Covered(a));
            if (c != 0) return c;
            c = Compare(b.Scores, a.Scores);
            if (c != 0) return c;
            c = a.Cost.CompareTo(b.Cost);
            if (c != 0) return c;
            c = Negatives(a).CompareTo(Negatives(b));
            return c != 0 ? c : a.Items.Count.CompareTo(b.Items.Count);
        });
        return best;
    }

    /// <summary>
    /// <paramref name="a"/> is at least as good as <paramref name="b"/> in every attribute (and not more
    /// expensive, when the cost matters) and better in at least one of them.
    /// </summary>
    private static bool Dominates(double[] a, long costA, double[] b, long costB, bool withCost)
    {
        var better = false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] < b[i] - Eps)
                return false;
            if (a[i] > b[i] + Eps)
                better = true;
        }
        if (withCost)
        {
            if (costA > costB)
                return false;
            if (costA < costB)
                better = true;
        }
        else if (!better && costA < costB)
            better = true;
        return better;
    }

    private static int Negatives(BuildResult r) => r.Items.Sum(i => i.Attrs.Count(a => a.Value < 0));

    private sealed record Pick(BuildCandidate Item, double[] Scores, long Cost);
}
