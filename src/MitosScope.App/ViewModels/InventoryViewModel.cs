using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MitosScope.App.Services;
using MitosScope.App.Settings;
using MitosScope.Core.Api;
using MitosScope.Core.Items;

namespace MitosScope.App.ViewModels;

/// <summary>
/// The inventory window: every owned item (/v1/inventory), grouped by category, with a search box.
/// Clicking an item shows its card in the middle of the window (attributes, description) with Equip /
/// Unequip (POST /v1/equip, needs the shop permission, not while alive in a match). A popup opened by
/// the small Inventory panel, like the shop. Loaded when shown, never polled. Use on the UI thread.
///
/// The Item sets tab keeps named sets of up to 3 equipment items (slots 0-2), made here and saved in
/// settings.json for each account: only MitosScope knows them, not the game server. Equipping a set
/// sends one POST /v1/equip per item that isn't equipped yet, each in the slot it must go in.
///
/// /v1/inventory is cached ~60 s by the server, so the equipped marks come from the displayed
/// session's equipment (/v1/equipment, never cached, also updated by every equip answer).
/// </summary>
public partial class InventoryViewModel : PopupViewModel
{
    private readonly AppSettings _settings;
    private readonly ItemSetStore _sets;
    private SessionViewModel? _session;
    private CancellationTokenSource? _loadCts;

    /// <summary>Category names from /v1/shop (player's language), per session; null until loaded.</summary>
    private Dictionary<int, string>? _categoryNames;

    /// <summary>All the groups loaded; <see cref="Groups"/> is what the search leaves of them.</summary>
    private IReadOnlyList<InventoryGroupViewModel>? _allGroups;

    public InventoryViewModel(TrackerViewModel tracker, AppSettings settings, ItemSetStore sets)
    {
        _settings = settings;
        _sets = sets;
        Tracker = tracker;
        tracker.PropertyChanged += OnTrackerChanged;
        sets.Changed += account =>
        {
            if (account == AccountId)
                RebuildSets();
        };
        Watch(tracker.Current);
    }

    public TrackerViewModel Tracker { get; }

    public override string Title => "Inventory";

    public override double Width => 820;
    public override double Height => 580;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(NoResults))]
    public partial IReadOnlyList<InventoryGroupViewModel>? Groups { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(NoResults))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty), nameof(NoResults))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;
    public bool IsEmpty => _allGroups is { Count: 0 } && !IsLoading && !HasError && IsItemsTab;

    public bool CanBrowse => _session is { IsConnected: true };

    private long? AccountId => _session?.Account?.Id;

    /// <summary>Second line of the small Inventory panel.</summary>
    public string CardText => _session switch
    {
        null => "No client connected",
        { IsConnected: false } => "Waiting for the game…",
        _ => IsOpen ? "Click to close" : "Click to open",
    };

    // ---- tabs and search ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItemsTab), nameof(IsEmpty), nameof(NoResults), nameof(SearchHint), nameof(ShowSearch))]
    public partial bool IsSetsTab { get; set; }

    public bool IsItemsTab => !IsSetsTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch), nameof(NoResults))]
    public partial string? SearchText { get; set; }

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);
    public bool NoResults => IsItemsTab && HasSearch && !IsLoading && Groups is { Count: 0 } && _allGroups is { Count: > 0 };
    public string SearchHint => IsEditing ? "Search your equipment to add to the set…" : "Search items (name, attribute, description)…";
    public bool ShowSearch => IsItemsTab || IsEditing;

    partial void OnSearchTextChanged(string? value)
    {
        ApplySearch();
        RebuildChoices();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    [RelayCommand]
    private void ShowItems() => IsSetsTab = false;

    [RelayCommand]
    private void ShowSets()
    {
        IsSetsTab = true;
        RebuildSets();
    }

    private void ApplySearch()
    {
        Groups = _allGroups is null ? null
            : HasSearch ? _allGroups.Select(g => g.Filter(SearchText!)).Where(g => g.Items.Count > 0).ToList()
            : _allGroups;
    }

    // ---- item card ------------------------------------------------------------------------------

    /// <summary>How long an error stays on the Equip button.</summary>
    private static readonly TimeSpan StatusTime = TimeSpan.FromSeconds(3);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem), nameof(ActionText), nameof(CanPressAction))]
    public partial InventoryItemViewModel? SelectedItem { get; private set; }

    public bool HasSelectedItem => SelectedItem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText), nameof(CanPressAction), nameof(CanEquipSets))]
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
        CancelEdit();
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
        if (HasSearch)
            return; // the search shows every matching category open, that isn't the player's choice
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
        if (IsBusy && SelectedItem is not null)
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

    // ---- item sets ------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSets), nameof(SetsCountText))]
    public partial IReadOnlyList<ItemSetViewModel>? Sets { get; private set; }

    public bool HasSets => Sets is { Count: > 0 };
    public string SetsCountText => $"{Sets?.Count ?? 0}/{ItemSetStore.MaxSets} sets · saved on this PC only";
    public bool CanEquipSets => !IsBusy && _session is { CanChangeEquipment: true };

    /// <summary>"Sets are equipped from the menu…" when equipping isn't possible now.</summary>
    public string SetsHint => _session switch
    {
        null or { IsConnected: false } => "No game client connected.",
        { HasShopScope: false } => "Equipping a set needs the shop permission.",
        { CanChangeEquipment: false } => "Sets can't be equipped while alive in a match.",
        _ => "",
    };

    public bool HasSetsHint => SetsHint.Length > 0;

    [ObservableProperty] public partial string? SetsStatus { get; private set; }

    /// <summary>The set being made or changed (null = a new one), shown in place of the list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSetList), nameof(SearchHint), nameof(ShowSearch), nameof(EditorTitle))]
    public partial bool IsEditing { get; private set; }

    private string? _editingId;

    [ObservableProperty] public partial string? EditName { get; set; }

    public IReadOnlyList<SetSlotViewModel> EditSlots { get; } = Enumerable.Range(0, ItemSets.Slots).Select(i => new SetSlotViewModel(i)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditTotals))]
    public partial IReadOnlyList<AttrLineViewModel>? EditTotals { get; private set; }

    public bool HasEditTotals => EditTotals is { Count: > 0 };

    /// <summary>The owned equipment that can go in a set (filtered by the search box).</summary>
    [ObservableProperty] public partial IReadOnlyList<InventoryItemViewModel>? EditChoices { get; private set; }

    [ObservableProperty] public partial string? EditError { get; private set; }

    public bool ShowSetList => !IsEditing;
    public string EditorTitle => _editingId is null ? "New item set" : "Edit item set";

    private IEnumerable<InventoryItemViewModel> OwnedEquipment =>
        _allGroups?.SelectMany(g => g.Items).Where(i => i.IsSetEquipment) ?? [];

    private void RebuildSets()
    {
        if (AccountId is not { } account)
        {
            Sets = null;
            return;
        }
        var owned = _allGroups?.SelectMany(g => g.Items).GroupBy(i => i.Item.Id).ToDictionary(g => g.Key, g => g.First());
        var equipped = (_session?.Session.Equipment ?? []).Where(e => e.Slot is >= 0 and < ItemSets.Slots).Select(e => e.Id).ToHashSet();
        var list = _sets.For(account);
        Sets = list.Select((s, i) => new ItemSetViewModel(s, owned, equipped, i, list.Count)).ToList();
        OnPropertyChanged(nameof(SetsHint));
        OnPropertyChanged(nameof(HasSetsHint));
    }

    [RelayCommand]
    private void NewSet() => StartEdit(null, []);

    /// <summary>A new set with what's equipped now in slots 0-2.</summary>
    [RelayCommand]
    private void NewSetFromEquipped()
    {
        var owned = OwnedEquipment.ToDictionary(i => i.Item.Id);
        var items = (_session?.Session.Equipment ?? [])
            .Where(e => e.Slot is >= 0 and < ItemSets.Slots)
            .OrderBy(e => e.Slot)
            .Select(e => owned.TryGetValue(e.Id, out var i) ? i.ToSetEntry()
                : new ItemSetEntry { Id = e.Id, Name = e.Name, Sprite = e.Sprite, Category = e.Category, Attrs = e.Attrs.ToList() })
            .ToList();
        StartEdit(null, items);
    }

    [RelayCommand]
    private void EditSet(ItemSetViewModel set) => StartEdit(set.Set, set.Set.Items);

    private void StartEdit(ItemSet? set, IReadOnlyList<ItemSetEntry> items)
    {
        if (AccountId is null)
            return;
        if (set is null && _sets.IsFull(AccountId.Value))
        {
            _ = ShowSetsStatus($"You have {ItemSetStore.MaxSets} sets: delete one first.");
            return;
        }
        _editingId = set?.Id;
        EditName = set?.Name ?? "";
        EditError = null;
        var owned = OwnedEquipment.ToDictionary(i => i.Item.Id);
        var equipped = (_session?.Session.Equipment ?? []).Select(e => e.Id).ToHashSet();
        for (var i = 0; i < EditSlots.Count; i++)
            EditSlots[i].Item = i < items.Count
                ? new ItemSetItemViewModel(items[i], owned.GetValueOrDefault(items[i].Id), equipped.Contains(items[i].Id))
                : null;
        SearchText = null;
        IsEditing = true;
        OnPropertyChanged(nameof(EditorTitle));
        UpdateEditTotals();
        RebuildChoices();
    }

    /// <summary>Puts an item in the set: in place of the one of its category (one per category), else the first empty slot.</summary>
    [RelayCommand]
    private void AddToSet(InventoryItemViewModel item)
    {
        EditError = null;
        if (EditSlots.Any(s => s.Item?.Entry.Id == item.Item.Id))
            return;
        var slot = EditSlots.FirstOrDefault(s => s.Item is { } i && i.Entry.Category == item.Item.Category)
                   ?? EditSlots.FirstOrDefault(s => s.Item is null);
        if (slot is null)
        {
            EditError = "The set is full (3 items): remove one first.";
            return;
        }
        slot.Item = new ItemSetItemViewModel(item.ToSetEntry(), item, item.IsEquipped);
        UpdateEditTotals();
        RebuildChoices();
    }

    [RelayCommand]
    private void RemoveFromSet(SetSlotViewModel slot)
    {
        slot.Item = null;
        EditError = null;
        UpdateEditTotals();
        RebuildChoices();
    }

    [RelayCommand]
    private void SaveSet()
    {
        if (AccountId is not { } account)
            return;
        var items = EditSlots.Where(s => s.Item is not null).Select(s => s.Item!.Entry).ToList();
        if (items.Count == 0)
        {
            EditError = "Add at least one item.";
            return;
        }
        if (_editingId is null)
        {
            if (_sets.Add(account, EditName, items) is null)
            {
                EditError = $"You have {ItemSetStore.MaxSets} sets already.";
                return;
            }
        }
        else
            _sets.Update(account, _editingId, EditName, items);
        CancelEdit();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        _editingId = null;
        EditError = null;
        EditChoices = null;
        if (HasSearch)
            SearchText = null;
    }

    private void UpdateEditTotals() =>
        EditTotals = ItemSets.SumAttrs(EditSlots.Where(s => s.Item is not null).Select(s => s.Item!.Attrs))
            .Select(a => new AttrLineViewModel(a, false)).ToList();

    private void RebuildChoices()
    {
        if (!IsEditing)
            return;
        var inSet = EditSlots.Where(s => s.Item is not null).Select(s => s.Item!.Entry.Id).ToHashSet();
        EditChoices = OwnedEquipment
            .Where(i => !inSet.Contains(i.Item.Id) && i.Matches(SearchText))
            .OrderByDescending(i => i.IsEquipped)
            .ThenBy(i => i.Item.Category)
            .ThenByDescending(i => i.Item.ItemLevel)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    [RelayCommand]
    private void StartRename(ItemSetViewModel set)
    {
        foreach (var other in Sets ?? [])
            other.IsRenaming = false;
        set.RenameText = set.Name;
        set.IsRenaming = true;
    }

    [RelayCommand]
    private void ConfirmRename(ItemSetViewModel set)
    {
        if (AccountId is { } account)
            _sets.Rename(account, set.Set.Id, set.RenameText);
        set.IsRenaming = false;
    }

    [RelayCommand]
    private void CancelRename(ItemSetViewModel set) => set.IsRenaming = false;

    [RelayCommand]
    private void AskDelete(ItemSetViewModel set) => set.IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete(ItemSetViewModel set) => set.IsConfirmingDelete = false;

    [RelayCommand]
    private void DeleteSet(ItemSetViewModel set)
    {
        if (AccountId is { } account)
            _sets.Delete(account, set.Set.Id);
    }

    [RelayCommand]
    private void MoveSetUp(ItemSetViewModel set)
    {
        if (AccountId is { } account)
            _sets.Move(account, set.Set.Id, -1);
    }

    [RelayCommand]
    private void MoveSetDown(ItemSetViewModel set)
    {
        if (AccountId is { } account)
            _sets.Move(account, set.Set.Id, 1);
    }

    /// <summary>
    /// Puts the set on: one POST /v1/equip per item not equipped yet, in the slot ItemSets.Plan picked
    /// (the one of the same category, else a free one). Items no longer owned are skipped. Stops at the
    /// first error (alive in a match, collector limit…), the items already changed stay on.
    /// </summary>
    [RelayCommand]
    private async Task EquipSetAsync(ItemSetViewModel set)
    {
        if (!CanEquipSets || _session is not { } session)
            return;
        var owned = OwnedEquipment.Select(i => i.Item.Id).ToHashSet();
        var usable = new ItemSet { Name = set.Name, Items = set.Set.Items.Where(i => owned.Contains(i.Id)).ToList() };
        var missing = set.Set.Items.Count - usable.Items.Count;
        var equipped = (session.Session.Equipment ?? []).Select(e => (e.Slot, e.Id, e.Category)).ToList();
        var steps = ItemSets.Plan(usable, equipped);

        if (steps.Count == 0)
        {
            await ShowEquipStatus(set, missing > 0 ? $"Nothing to equip ({missing} not owned)" : "Already equipped");
            return;
        }

        IsBusy = true;
        string? error = null;
        for (var i = 0; i < steps.Count; i++)
        {
            set.EquipStatus = $"Equipping {i + 1}/{steps.Count}…";
            var r = await session.Session.EquipAsync(steps[i].ItemId, steps[i].Slot, unequip: false, CancellationToken.None);
            if (_session != session)
                break;
            if (r.StatusCode is >= 200 and < 300 || r.Error == "addon_already_equipped")
                continue;
            error = SessionViewModel.EquipErrorText(r.StatusCode, r.Error, unequip: false);
            break;
        }
        IsBusy = false;
        if (_session != session)
            return;

        RebuildSets();
        var shown = Sets?.FirstOrDefault(s => s.Set.Id == set.Set.Id) ?? set;
        await ShowEquipStatus(shown, error ?? (missing > 0 ? $"Equipped ({missing} not owned)" : "Equipped"));
    }

    private static async Task ShowEquipStatus(ItemSetViewModel set, string status)
    {
        set.EquipStatus = status;
        await Task.Delay(StatusTime);
        if (set.EquipStatus == status)
            set.EquipStatus = null;
    }

    private async Task ShowSetsStatus(string status)
    {
        SetsStatus = status;
        await Task.Delay(StatusTime);
        if (SetsStatus == status)
            SetsStatus = null;
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
        _allGroups = r.Value!.Data
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
        ApplySearch();
        ApplyEquipped();
        if (selectedId is { } id)
            SelectedItem = _allGroups.SelectMany(g => g.Items).FirstOrDefault(i => i.Item.Id == id);
        OnPropertyChanged(nameof(IsEmpty));
        RebuildSets();
        RebuildChoices();
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
        if (_allGroups is null)
            return;
        var slots = _session?.Session.Equipment?
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.First().Slot);
        foreach (var item in _allGroups.SelectMany(g => g.Items))
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

        // Another account: other items, sets and category language.
        _categoryNames = null;
        IsBusy = false;
        CloseItem();
        CancelEdit();
        _allGroups = null;
        Groups = null;
        RebuildSets();
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
                if (!IsBusy)
                    RebuildSets();
                break;
            case nameof(SessionViewModel.Account):
                RebuildSets(); // the account id arrives after the connection
                break;
            case nameof(SessionViewModel.IsConnected):
                NotifyAccess();
                if (IsOpen && CanBrowse && _allGroups is null && !IsLoading)
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
        OnPropertyChanged(nameof(CanEquipSets));
        OnPropertyChanged(nameof(SetsHint));
        OnPropertyChanged(nameof(HasSetsHint));
    }
}
