using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MitosScope.App.Settings;
using MitosScope.Core.Api;

namespace MitosScope.App.ViewModels;

/// <summary>
/// The inventory window: every owned item (/v1/inventory), grouped by category. Clicking an item
/// shows its card in the middle of the window, with Equip / Unequip (POST /v1/equip, needs the
/// shop permission, not while alive in a match). A popup opened by the small Inventory panel,
/// like the shop. Loaded when shown, never polled. Use on the UI thread.
///
/// /v1/inventory is cached ~60 s by the server, so the equipped marks come from the displayed
/// session's equipment (/v1/equipment, never cached, also updated by every equip answer).
/// </summary>
public partial class InventoryViewModel : PopupViewModel
{
    private readonly AppSettings _settings;
    private SessionViewModel? _session;
    private CancellationTokenSource? _loadCts;

    /// <summary>Category names from /v1/shop (player's language), per session; null until loaded.</summary>
    private Dictionary<int, string>? _categoryNames;

    public InventoryViewModel(TrackerViewModel tracker, AppSettings settings)
    {
        _settings = settings;
        Tracker = tracker;
        tracker.PropertyChanged += OnTrackerChanged;
        Watch(tracker.Current);
    }

    public TrackerViewModel Tracker { get; }

    public override string Title => "Inventory";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<InventoryGroupViewModel>? Groups { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;
    public bool IsEmpty => Groups is { Count: 0 } && !IsLoading && !HasError;

    public bool CanBrowse => _session is { IsConnected: true };

    /// <summary>Second line of the small Inventory panel.</summary>
    public string CardText => _session switch
    {
        null => "No client connected",
        { IsConnected: false } => "Waiting for the game…",
        _ => IsOpen ? "Click to close" : "Click to open",
    };

    // ---- item card ------------------------------------------------------------------------------

    /// <summary>How long an error stays on the Equip button.</summary>
    private static readonly TimeSpan StatusTime = TimeSpan.FromSeconds(3);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem), nameof(ActionText), nameof(CanPressAction))]
    public partial InventoryItemViewModel? SelectedItem { get; private set; }

    public bool HasSelectedItem => SelectedItem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(CanPressAction))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Shown on the button instead of Equip / Unequip for a few seconds (errors).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    public partial string? ActionStatus { get; private set; }

    public string ActionText => SelectedItem switch
    {
        null => "",
        _ when ActionStatus is { } s => s,
        { IsEquipped: true } => IsBusy ? "Unequipping…" : "Unequip",
        _ => IsBusy ? "Equipping…" : "Equip",
    };

    public bool CanPressAction => SelectedItem is not null && !IsBusy && _session is { CanChangeEquipment: true };

    /// <summary>Why the button is disabled (shown under it), or "".</summary>
    public string ActionHint => _session switch
    {
        { HasShopScope: false } => "Changing equipment needs the shop permission.",
        { CanChangeEquipment: false } => "Equipment can't be changed while alive in a match.",
        _ => "",
    };

    public bool HasActionHint => ActionHint.Length > 0;

    // ---- commands -------------------------------------------------------------------------------

    protected override void OnOpened()
    {
        OnPropertyChanged(nameof(CardText));
        _ = LoadAsync();
    }

    protected override void OnClosed()
    {
        CloseItem();
        CancelLoad();
        IsLoading = false;
        OnPropertyChanged(nameof(CardText));
    }

    [RelayCommand]
    private Task Retry() => LoadAsync();

    /// <summary>Shows or hides a category's items; the choice is saved in settings.json.</summary>
    [RelayCommand]
    private void ToggleGroup(InventoryGroupViewModel group)
    {
        group.IsExpanded = !group.IsExpanded;
        var collapsed = _settings.CollapsedInventoryCategories;
        collapsed.Remove(group.Category);
        if (!group.IsExpanded)
            collapsed.Add(group.Category);
        SettingsStore.Save(_settings);
    }

    [RelayCommand]
    private void SelectItem(InventoryItemViewModel item)
    {
        if (IsBusy)
            return;
        ActionStatus = null;
        SelectedItem = item;
    }

    [RelayCommand]
    private void CloseItem()
    {
        if (IsBusy)
            return; // keep the card until the answer comes
        ActionStatus = null;
        SelectedItem = null;
    }

    /// <summary>
    /// Equips (the server picks the slot) or unequips the item of the card. The answer's equipped
    /// slots go to the session's equipment, which updates the marks here (OnSessionChanged).
    /// </summary>
    [RelayCommand]
    private async Task ToggleEquipAsync()
    {
        if (!CanPressAction || SelectedItem is not { } item || _session is not { } session)
            return;
        var unequip = item.IsEquipped;
        IsBusy = true;
        var r = await session.Session.EquipAsync(item.Item.Id, null, unequip, CancellationToken.None);
        IsBusy = false;
        if (_session != session)
            return;
        if (r.StatusCode is >= 200 and < 300)
        {
            // Older answers without "equip": mark at least this item from the answer itself.
            if (r.Value is { Equip: null } v)
            {
                item.IsEquipped = v.Equipped;
                item.Slot = v.Equipped ? v.Slot : -1;
            }
            OnPropertyChanged(nameof(ActionText));
            return;
        }

        var status = SessionViewModel.EquipErrorText(r.StatusCode, r.Error, unequip);
        ActionStatus = status;
        await Task.Delay(StatusTime);
        if (ActionStatus == status)
            ActionStatus = null;
    }

    // ---- loading --------------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        CancelLoad();
        ErrorText = null;
        if (!CanBrowse)
        {
            IsLoading = false;
            ErrorText = "No game client connected.";
            return;
        }
        var session = _session!;
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        IsLoading = true;

        if (_categoryNames is null && session.HasShopScope)
        {
            var c = await session.Session.GetShopCategoriesAsync(ct);
            if (ct.IsCancellationRequested)
                return;
            if (c.IsSuccess)
                _categoryNames = c.Value!.Categories.ToDictionary(x => x.Id, x => x.Name ?? "");
        }

        var r = await session.Session.GetInventoryAsync(ct);
        if (ct.IsCancellationRequested)
            return;
        IsLoading = false;
        if (!r.IsSuccess)
        {
            ErrorText = r.StatusCode switch
            {
                0 => "The game didn't answer. Try again.",
                504 => "The game servers didn't answer in time. Try again.",
                _ => $"Couldn't load the inventory ({r.Error ?? r.StatusCode.ToString()}).",
            };
            return;
        }

        var selectedId = SelectedItem?.Item.Id;
        Groups = r.Value!.Data
            .GroupBy(i => i.Category)
            .OrderBy(g => g.Key)
            .Select(g => new InventoryGroupViewModel(
                g.Key,
                CategoryName(g.Key),
                g.Select(i => new InventoryItemViewModel(i))
                 .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                 .ToList(),
                expanded: !_settings.CollapsedInventoryCategories.Contains(g.Key)))
            .ToList();
        ApplyEquipped();
        if (selectedId is { } id)
            SelectedItem = Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Item.Id == id);
    }

    private string CategoryName(int id) =>
        _categoryNames?.GetValueOrDefault(id) is { Length: > 0 } name ? name
        : id switch
        {
            6 => "Items",
            8 => "Evolutions",
            10 => "Gems",
            _ => $"Category {id}",
        };

    /// <summary>Equipped marks from the session's equipment (fresh), or the inventory's flags without it.</summary>
    private void ApplyEquipped()
    {
        if (Groups is null)
            return;
        var slots = _session?.Session.Equipment?
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First().Slot);
        foreach (var item in Groups.SelectMany(g => g.Items))
            item.ApplyEquipped(slots);
        OnPropertyChanged(nameof(ActionText));
    }

    private void CancelLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
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

        // Another account: other items and category language.
        _categoryNames = null;
        IsBusy = false;
        CloseItem();
        Groups = null;
        if (IsOpen)
            _ = LoadAsync();
        NotifyAccess();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionViewModel.EquippedItems):
                ApplyEquipped(); // equip/unequip here, in the Equipment panel or in the game
                break;
            case nameof(SessionViewModel.IsConnected):
                NotifyAccess();
                if (IsOpen && CanBrowse && Groups is null && !IsLoading)
                    _ = LoadAsync();
                break;
            case nameof(SessionViewModel.CanChangeEquipment) or nameof(SessionViewModel.HasShopScope):
                NotifyAccess();
                break;
        }
    }

    private void NotifyAccess()
    {
        OnPropertyChanged(nameof(CardText));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanPressAction));
        OnPropertyChanged(nameof(ActionHint));
        OnPropertyChanged(nameof(HasActionHint));
    }
}
