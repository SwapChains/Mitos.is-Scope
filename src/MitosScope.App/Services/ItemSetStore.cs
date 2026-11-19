using System;
using System.Collections.Generic;
using System.Linq;
using MitosScope.App.Settings;
using MitosScope.Core.Items;

namespace MitosScope.App.Services;

/// <summary>
/// The item sets of every account, saved in settings.json (only on this PC: the game server doesn't
/// know about them). Shared by the inventory window and the shop build tool. Use on the UI thread.
/// </summary>
public sealed class ItemSetStore(AppSettings settings)
{
    /// <summary>Most sets per account (the list must stay usable in the window).</summary>
    public const int MaxSets = 50;

    /// <summary>Raised after any change, with the account id.</summary>
    public event Action<long>? Changed;

    public IReadOnlyList<ItemSet> For(long accountId) =>
        settings.ItemSets.TryGetValue(Key(accountId), out var list) ? list : [];

    public bool IsFull(long accountId) => For(accountId).Count >= MaxSets;

    /// <summary>A new set at the end of the list; null when the account has <see cref="MaxSets"/> already.</summary>
    public ItemSet? Add(long accountId, string? name, IEnumerable<ItemSetEntry> items)
    {
        if (!settings.ItemSets.TryGetValue(Key(accountId), out var list))
            settings.ItemSets[Key(accountId)] = list = [];
        if (list.Count >= MaxSets)
            return null;
        var set = new ItemSet { Name = CleanName(name, list), Items = items.Take(ItemSets.Slots).ToList(), Updated = Now() };
        list.Add(set);
        Save(accountId);
        return set;
    }

    public void Update(long accountId, string setId, string? name, IEnumerable<ItemSetEntry> items)
    {
        if (Find(accountId, setId) is not { } set)
            return;
        set.Name = CleanName(name, For(accountId).Where(s => s != set), set.Name);
        set.Items = items.Take(ItemSets.Slots).ToList();
        set.Updated = Now();
        Save(accountId);
    }

    public void Rename(long accountId, string setId, string? name)
    {
        if (Find(accountId, setId) is not { } set)
            return;
        set.Name = CleanName(name, For(accountId).Where(s => s != set), set.Name);
        set.Updated = Now();
        Save(accountId);
    }

    public void Delete(long accountId, string setId)
    {
        if (!settings.ItemSets.TryGetValue(Key(accountId), out var list))
            return;
        list.RemoveAll(s => s.Id == setId);
        Save(accountId);
    }

    /// <summary>Moves a set one place up (-1) or down (+1) in the list.</summary>
    public void Move(long accountId, string setId, int delta)
    {
        if (!settings.ItemSets.TryGetValue(Key(accountId), out var list))
            return;
        var i = list.FindIndex(s => s.Id == setId);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count)
            return;
        (list[i], list[j]) = (list[j], list[i]);
        Save(accountId);
    }

    public ItemSet? Find(long accountId, string setId) => For(accountId).FirstOrDefault(s => s.Id == setId);

    /// <summary>Trimmed, at most <see cref="ItemSets.NameMax"/> characters; empty = "Set N" (the first free number).</summary>
    private static string CleanName(string? name, IEnumerable<ItemSet> others, string? fallback = null)
    {
        var clean = (name ?? "").Trim();
        if (clean.Length > ItemSets.NameMax)
            clean = clean[..ItemSets.NameMax].TrimEnd();
        if (clean.Length > 0)
            return clean;
        if (!string.IsNullOrEmpty(fallback))
            return fallback;
        var used = others.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (used.Contains($"Set {n}"))
            n++;
        return $"Set {n}";
    }

    private void Save(long accountId)
    {
        SettingsStore.Save(settings);
        Changed?.Invoke(accountId);
    }

    private static string Key(long accountId) => accountId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
