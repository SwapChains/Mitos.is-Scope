using System.Globalization;
using System.Text.RegularExpressions;
using MitosScope.Core.Api;

namespace MitosScope.Core.Items;

/// <summary>Search and description helpers shared by the shop and the inventory windows.</summary>
public static partial class ItemText
{
    /// <summary>
    /// Every word of the query is found in the name, the description or an attribute (its name in the
    /// player's language or the game's id), ignoring case and accents. An empty query matches everything.
    /// </summary>
    public static bool Matches(string? query, string? name, string? description, IEnumerable<ItemAttr>? attrs)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;
        var text = Fold(string.Join(' ', new[] { name, description }
            .Concat(attrs?.SelectMany(a => new[] { a.Label, a.Name, a.Name.Replace('_', ' ') }) ?? [])));
        return Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(text.Contains);
    }

    /// <summary>
    /// The item's description without the "+12 Speed" lines the game writes in it, now that the
    /// attributes are shown on their own. Null when nothing is left.
    /// </summary>
    public static string? Description(string? description, bool hasAttrs)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;
        var lines = description.Replace("\r", "").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !(hasAttrs && AttrLine().IsMatch(l)));
        var clean = string.Join('\n', lines).Trim();
        return clean.Length > 0 ? clean : null;
    }

    private static string Fold(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return "";
        var decomposed = s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var chars = decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(chars);
    }

    [GeneratedRegex(@"^[+\-−]\s?\d+([.,]\d+)?\s?%?(\s?pt)?\b")]
    private static partial Regex AttrLine();
}
