using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MitosScope.Core.Api;

namespace MitosScope.App.ViewModels;

/// <summary>
/// Gem training (API v4): the 3 training slots and the owned gems. Shown compact by the Gem
/// Training panel and in full by its popup window, where clicking a gem opens its card with
/// "Train in slot N" / "Remove from slot N" (POST /v1/gemtraining, needs the shop permission;
/// slot 1 equips the gem, so not while alive in a match).
///
/// The data comes from the displayed session (/v1/gemtraining is polled every 15 s, never cached),
/// and is re-read when the window opens. Use on the UI thread.
/// </summary>
public partial class GemTrainingViewModel : PopupViewModel
{
    private SessionViewModel? _session;
    private CancellationTokenSource? _loadCts;

    /// <summary>How long an error stays on the gem card / slot card.</summary>
    private static readonly TimeSpan StatusTime = TimeSpan.FromSeconds(3);

    public GemTrainingViewModel(TrackerViewModel tracker)
    {
        Tracker = tracker;
        tracker.PropertyChanged += OnTrackerChanged;
        Watch(tracker.Current);
    }

    public TrackerViewModel Tracker { get; }

    public override string Title => "Gem Training";

    [ObservableProperty] public partial IReadOnlyList<GemSlotViewModel>? Slots { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<OwnedGemViewModel>? Gems { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    public bool HasError => ErrorText is not null;

    private GemTraining? Data => _session?.GemTraining;

    public bool HasData => Data is { Enabled: true };

    /// <summary>"+20% bonus · 4 gems owned · 1 training".</summary>
    public string BonusText => Data?.Bonus is { } b
        ? $"+{b.BonusPct:N0}% bonus · {b.OwnedCount:N0} gems owned · {b.TrainingCount:N0} training"
        : "";

    public string RateText => Data is { ExpPerHour: > 0 } d ? $"{d.ExpPerHour:N0} Gem EXP per hour in training" : "";

    public bool HasNoGems => HasData && Gems is { Count: 0 };

    /// <summary>Panel / window message when there's nothing to show, or "".</summary>
    public string StateText => _session switch
    {
        null => "No client connected",
        { IsConnected: false } => "Waiting for the game…",
        { GemTraining: null } => "Gem training not available",
        { GemTraining.Enabled: false } => "Gem training is off for this account",
        _ => "",
    };

    public bool HasStateText => StateText.Length > 0;

    /// <summary>Last line of the small panel.</summary>
    public string CardText => IsOpen ? "Click to close" : "Click to open";

    // ---- gem card -------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGem), nameof(Actions))]
    public partial OwnedGemViewModel? SelectedGem { get; private set; }

    public bool HasSelectedGem => SelectedGem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Actions))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Training…", errors: shown on the gem card for a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionStatus))]
    public partial string? ActionStatus { get; private set; }

    public bool HasActionStatus => ActionStatus is not null;

    /// <summary>One button per unlocked slot: put the selected gem there, or take it out of its slot.</summary>
    public IReadOnlyList<GemSlotAction> Actions
    {
        get
        {
            if (SelectedGem is not { } gem || Slots is null)
                return [];
            var canWrite = _session is { HasShopScope: true } && !IsBusy;
            var alive = _session?.Game?.Alive == true;
            var list = new List<GemSlotAction>();
            foreach (var slot in Slots.Where(s => !s.IsLocked))
            {
                var n = slot.Number;
                if (gem.Gem.Training == n)
                {
                    list.Add(new GemSlotAction(n, true, $"Remove from slot {n}",
                        canWrite && !(n == 1 && alive), n == 1 && alive ? "Not while alive in a match" : null));
                    continue;
                }
                string? hint = null;
                if (gem.Gem.Training > 0)
                    hint = $"Remove it from slot {gem.Gem.Training} first";
                else if (n > 1 && gem.Gem.PlayOnly)
                    hint = "Last level gems only go in slot 1";
                else if (n == 1 && alive)
                    hint = "Not while alive in a match";
                var text = slot.HasGem ? $"Train in slot {n} (replaces {slot.GemName})" : $"Train in slot {n}";
                list.Add(new GemSlotAction(n, false, text, canWrite && hint is null, hint));
            }
            return list;
        }
    }

    public string PermissionHint => _session is { HasShopScope: false } ? "Changing gem training needs the shop permission." : "";
    public bool HasPermissionHint => PermissionHint.Length > 0;

    // ---- commands -------------------------------------------------------------------------------

    protected override void OnOpened()
    {
        OnPropertyChanged(nameof(CardText));
        _ = LoadAsync();
    }

    protected override void OnClosed()
    {
        CloseGem();
        _loadCts?.Cancel();
        IsLoading = false;
        OnPropertyChanged(nameof(CardText));
    }

    [RelayCommand]
    private Task Retry() => LoadAsync();

    [RelayCommand]
    private void SelectGem(OwnedGemViewModel gem)
    {
        if (IsBusy)
            return;
        ActionStatus = null;
        SelectedGem = gem;
    }

    [RelayCommand]
    private void CloseGem()
    {
        if (IsBusy)
            return;
        ActionStatus = null;
        SelectedGem = null;
    }

    /// <summary>Button of the gem card.</summary>
    [RelayCommand]
    private async Task ApplyActionAsync(GemSlotAction action)
    {
        if (!action.IsEnabled || IsBusy || SelectedGem is not { } gem || _session is not { } session)
            return;
        IsBusy = true;
        ActionStatus = action.Remove ? "Removing…" : "Training…";
        var r = await session.Session.SetGemTrainingAsync(action.Slot, action.Remove ? null : gem.Gem.Id, CancellationToken.None);
        IsBusy = false;
        if (_session != session)
            return;
        if (r.StatusCode is >= 200 and < 300)
        {
            ActionStatus = null; // the slots and the card are rebuilt from the answer
            return;
        }
        var status = DescribeError(r.StatusCode, r.Error);
        ActionStatus = status;
        await Task.Delay(StatusTime);
        if (ActionStatus == status)
            ActionStatus = null;
    }

    /// <summary>"Remove" on a slot card (active or completed slot).</summary>
    [RelayCommand]
    private async Task RemoveFromSlotAsync(GemSlotViewModel slot)
    {
        if (IsBusy || !slot.HasGem || _session is not { HasShopScope: true } session)
            return;
        if (slot.Number == 1 && session.Game?.Alive == true)
        {
            slot.Status = "Not while alive in a match";
            await Task.Delay(StatusTime);
            slot.Status = null;
            return;
        }
        IsBusy = true;
        slot.Status = "Removing…";
        var r = await session.Session.SetGemTrainingAsync(slot.Number, null, CancellationToken.None);
        IsBusy = false;
        if (r.StatusCode is >= 200 and < 300)
            return; // the slots are rebuilt from the answer
        slot.Status = DescribeError(r.StatusCode, r.Error);
        await Task.Delay(StatusTime);
        slot.Status = null;
    }

    private static string DescribeError(int status, string? error) => (status, error) switch
    {
        (0, "not_connected") => "Not connected",
        (0, _) => "Game not responding",
        (403, "scope_required" or "addon_scope_revoked") => "No shop permission",
        (409, _) => "Not while alive in a match",
        (_, "gem_training_slot_locked") => "This slot isn't unlocked",
        (_, "gem_training_invalid") => "This gem can't go in that slot",
        (_, "gem_training_already") => "The gem already trains in another slot",
        (_, "invalid_level") => "Your level is too low for this gem",
        (_, "no_equip_day") => "Equipment can't be changed today",
        (504, _) => "Server timeout, try again",
        _ => $"Failed ({error ?? status.ToString()})",
    };

    // ---- loading --------------------------------------------------------------------------------

    /// <summary>Re-reads gem training when the window opens (the poll is every 15 s).</summary>
    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        ErrorText = null;
        if (_session is not { IsConnected: true } session)
            return; // StateText explains it
        IsLoading = true;
        var r = await session.Session.RefreshGemTrainingAsync(ct);
        if (ct.IsCancellationRequested)
            return;
        IsLoading = false;
        // 404: client without gem training (StateText says so). Other errors: keep the polled data.
        if (!r.IsSuccess && r.StatusCode != 404 && Data is null)
            ErrorText = r.StatusCode == 0 ? "The game didn't answer. Try again." : $"Couldn't load gem training ({r.Error ?? r.StatusCode.ToString()}).";
    }

    /// <summary>Rebuilds slots and gems from the session's data.</summary>
    private void Rebuild()
    {
        var data = Data is { Enabled: true } d ? d : null;
        var owned = data?.Owned ?? [];
        Slots = data?.Slots
            .OrderBy(s => s.Slot)
            .Select(s => new GemSlotViewModel(s, owned.FirstOrDefault(o => o.Id == s.Gem?.Id)))
            .ToList();
        Gems = owned
            .Select(g => new OwnedGemViewModel(g))
            .OrderByDescending(g => g.IsTraining)
            .ThenByDescending(g => g.Gem.Level)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (SelectedGem is { } selected)
            SelectedGem = Gems.FirstOrDefault(g => g.Gem.Id == selected.Gem.Id);
        NotifyAll();
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(BonusText));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(HasNoGems));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasStateText));
        OnPropertyChanged(nameof(Actions));
        OnPropertyChanged(nameof(PermissionHint));
        OnPropertyChanged(nameof(HasPermissionHint));
    }

    // ---- following the displayed client ---------------------------------------------------------

    private void OnTrackerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackerViewModel.Current))
            Watch(Tracker.Current);
    }

    private void Watch(SessionViewModel? session)
    {
        if (_session == session)
            return;
        if (_session is not null)
            _session.PropertyChanged -= OnSessionChanged;
        _session = session;
        if (_session is not null)
            _session.PropertyChanged += OnSessionChanged;

        IsBusy = false;
        CloseGem();
        Rebuild();
        if (IsOpen)
            _ = LoadAsync();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionViewModel.GemTraining):
                Rebuild();
                break;
            case nameof(SessionViewModel.IsConnected) or nameof(SessionViewModel.HasShopScope)
                or nameof(SessionViewModel.CanChangeEquipment): // alive changed
                NotifyAll();
                break;
        }
    }
}
