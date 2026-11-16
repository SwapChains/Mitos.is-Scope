using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MitosScope.App.Services;
using MitosScope.Core.Api;

namespace MitosScope.App.ViewModels;

/// <summary>One gem training slot (rebuilt on each /v1/gemtraining answer).</summary>
public sealed partial class GemSlotViewModel : ObservableObject
{
    public GemSlotViewModel(GemTrainingSlot slot, OwnedGem? owned)
    {
        Slot = slot;
        // The slot's gem may come without a sprite: take it from the owned gem with the same id.
        Sprite = ItemSprites.Get(slot.Gem?.Sprite ?? owned?.Sprite);
        GemName = slot.Gem is { } g ? EquippedItemViewModel.CleanName(g.Name ?? owned?.Name) : "";
    }

    public GemTrainingSlot Slot { get; }
    public int Number => Slot.Slot;
    public TrainingGem? Gem => Slot.Gem;
    public IImage? Sprite { get; }
    public string GemName { get; }

    public bool IsLocked => Slot.State == "locked";
    public bool IsCompleted => Slot.State == "completed";
    public bool HasGem => Gem is not null && !IsLocked;
    public bool IsEmpty => !HasGem && !IsLocked;

    /// <summary>"Free", "Premium pass", "Coin pack".</summary>
    public string UnlockText => Slot.Unlock switch
    {
        "free" => "Free",
        "premium" => "Premium pass",
        "coinpack" => "Coin pack",
        { Length: > 0 } u => u,
        _ => "",
    };

    public string HeaderText => UnlockText.Length > 0 ? $"Slot {Number} · {UnlockText}" : $"Slot {Number}";

    /// <summary>Under the gem: "Level up!", "Locked: needs the premium pass", "Empty".</summary>
    public string StateText => Slot.State switch
    {
        "completed" => "Level up!",
        "locked" => Slot.Unlock switch
        {
            "premium" => "Locked: needs the premium battle pass",
            "coinpack" => "Locked: needs a coin pack this month",
            _ => "Locked",
        },
        _ when Gem is null => "Empty",
        _ => "",
    };

    public bool HasStateText => StateText.Length > 0;

    public string LevelText => Gem is { } g ? $"Level {g.Level:N0}" : "";
    public double ExpFraction => Gem is { ExpMax: > 0 } g ? Math.Clamp((double)g.Exp / g.ExpMax, 0, 1) : 0;
    public string ExpText => Gem is { } g ? $"{g.Exp:N0} / {g.ExpMax:N0}" : "";

    /// <summary>"Next level in 18h 0m".</summary>
    public string EtaText => Gem is { Eta: > 0 } g && !IsCompleted ? $"Next level in {Duration.Format(g.Eta)}" : "";
    public bool HasEta => EtaText.Length > 0;

    /// <summary>Short ETA for the overlay panel: "18h 0m".</summary>
    public string ShortEtaText => Gem is { Eta: > 0 } g && !IsCompleted ? Duration.Format(g.Eta) : IsCompleted ? "Level up!" : "";

    /// <summary>"Unlock ends in 1d 4h" (slots 2-3 unlocked for a time).</summary>
    public string UntilText => Slot.Until > 0 && !IsLocked ? $"Unlock ends in {Duration.Format(Slot.Until)}" : "";
    public bool HasUntil => UntilText.Length > 0;

    /// <summary>One line for the overlay panel: "Red gem · Lv 3".</summary>
    public string SummaryText => HasGem ? $"{GemName} · Lv {Gem!.Level:N0}" : IsLocked ? "Locked" : "Empty";

    /// <summary>"Removing…" or why it failed, on the slot card for a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string? Status { get; set; }

    public bool HasStatus => Status is not null;
}

/// <summary>One owned gem in the gem training window.</summary>
public sealed class OwnedGemViewModel(OwnedGem gem)
{
    public OwnedGem Gem { get; } = gem;
    public string Name { get; } = EquippedItemViewModel.CleanName(gem.Name);
    public IImage? Sprite { get; } = ItemSprites.Get(gem.Sprite);

    public string LevelText => $"Level {Gem.Level:N0}";
    public double ExpFraction => Gem.ExpMax > 0 ? Math.Clamp((double)Gem.Exp / Gem.ExpMax, 0, 1) : 0;
    public string ExpText => Gem.PlayOnly ? "Last level" : $"{Gem.Exp:N0} / {Gem.ExpMax:N0} Gem EXP";

    public bool IsTraining => Gem.Training > 0;
    public bool IsEquipped => Gem.Equipped;

    /// <summary>"Training in slot 2", "Equipped", or "".</summary>
    public string StateText => IsTraining ? $"Training · slot {Gem.Training}" : IsEquipped ? "Equipped" : "";
    public bool HasStateText => StateText.Length > 0;

    /// <summary>Extra notes on the gem card.</summary>
    public string NotesText
    {
        get
        {
            var notes = new List<string>();
            if (Gem.PlayOnly)
                notes.Add("Last level: it only grows by playing, so it can't go in slots 2-3.");
            if (Gem.ReqLevel > 0)
                notes.Add($"Requires player level {Gem.ReqLevel:N0}.");
            return string.Join("\n", notes);
        }
    }

    public bool HasNotes => NotesText.Length > 0;
}

/// <summary>A button on the gem card: put the gem in a slot, or take it out of its slot.</summary>
public sealed record GemSlotAction(int Slot, bool Remove, string Text, bool IsEnabled, string? Hint)
{
    public bool HasHint => Hint is not null;
}

internal static class Duration
{
    /// <summary>"2d 4h", "18h 5m", "12m 30s", "40s".</summary>
    public static string Format(long seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h"
            : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}m"
            : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
            : $"{t.Seconds}s";
    }
}
