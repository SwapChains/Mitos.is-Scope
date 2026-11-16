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
/// The shop browser (API v3, needs the "shop" permission): a popup opened by the small Shop panel.
/// First page: the categories; clicking one shows its items in the same window. Clicking an item
/// shows its card in the middle of the window, with the Buy button (and how many packs, for
/// consumables). Everything is loaded when shown, never polled. Use on the UI thread.
/// </summary>
public partial class ShopViewModel : PopupViewModel
{
    private SessionViewModel? _session;
    private CancellationTokenSource? _loadCts;

    public ShopViewModel(TrackerViewModel tracker)
    {
        Tracker = tracker;
        tracker.PropertyChanged += OnTrackerChanged;
        Watch(tracker.Current);
    }

    public TrackerViewModel Tracker { get; }

    public override string Title => "Shop";

    [ObservableProperty] public partial IReadOnlyList<ShopCategory>? Categories { get; private set; }

    /// <summary>Null = categories page; otherwise the items page of this category.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategoriesPage), nameof(IsItemsPage), nameof(TitleText))]
    public partial ShopCategory? SelectedCategory { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<ShopItemViewModel>? Items { get; private set; }

    /// <summary>Coins from the last items answer (not cached by the server).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CoinsText), nameof(HasCoins))]
    public partial long? Coins { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; }

    // ---- item card (buying) ---------------------------------------------------------------------

    /// <summary>Most packs of a consumable bought at once from the card.</summary>
    public const int MaxQuantity = 30;

    /// <summary>How long "Bought" or an error stays on the Buy button.</summary>
    private static readonly TimeSpan BuyStatusTime = TimeSpan.FromSeconds(3);

    /// <summary>The item whose card is shown over the list, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem), nameof(Quantity), nameof(TotalPrice), nameof(PacksText),
        nameof(BuyText), nameof(CanPressBuy), nameof(ShowBuy), nameof(ShowQuantity))]
    public partial ShopItemViewModel? SelectedItem { get; private set; }

    public bool HasSelectedItem => SelectedItem is not null;

    /// <summary>Slider value (1-30). Only consumables have the slider; other items are always 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Quantity), nameof(TotalPrice), nameof(PacksText), nameof(BuyText), nameof(CanPressBuy))]
    public partial double QuantityValue { get; set; } = 1;

    public int Quantity => SelectedItem is { IsConsumable: true } ? Math.Clamp((int)Math.Round(QuantityValue), 1, MaxQuantity) : 1;

    public long TotalPrice => (SelectedItem?.Item.Price ?? 0) * Quantity;

    public bool ShowQuantity => SelectedItem is { IsConsumable: true, IsBuyable: true };

    /// <summary>"3 packs · 15 uses".</summary>
    public string PacksText
    {
        get
        {
            var packs = Quantity == 1 ? "1 pack" : $"{Quantity:N0} packs";
            return SelectedItem?.Item.Pack is > 1 and var p ? $"{packs} · {p * Quantity:N0} uses" : packs;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPressBuy), nameof(BuyText))]
    public partial bool IsBuying { get; private set; }

    /// <summary>Shown on the Buy button instead of the price for a few seconds ("Buying…", "Bought", errors).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuyText), nameof(ShowBuy))]
    public partial string? BuyStatus { get; private set; }

    private bool CanAfford => Coins is not { } c || TotalPrice <= c;

    /// <summary>The Buy button is shown when the item can be bought, or while it shows a result.</summary>
    public bool ShowBuy => SelectedItem is { IsBuyable: true } || BuyStatus is not null;

    public bool CanPressBuy => SelectedItem is { IsBuyable: true } && CanAfford && !IsBuying && CanBrowse;

    public string BuyText => BuyStatus ?? (CanAfford ? $"Buy · {TotalPrice:N0} coins" : $"Not enough coins ({TotalPrice:N0})");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool IsCategoriesPage => SelectedCategory is null;
    public bool IsItemsPage => SelectedCategory is not null;
    public bool HasError => ErrorText is not null;
    public bool HasCoins => Coins is not null;
    public string CoinsText => Coins is { } c ? $"{c:N0} coins" : "";
    public string TitleText => SelectedCategory?.Name is { Length: > 0 } n ? n : "Shop";

    /// <summary>Second line of the small Shop panel.</summary>
    public string CardText => _session switch
    {
        null => "No client connected",
        { IsConnected: false } => "Waiting for the game…",
        { HasShopScope: false } => "Shop permission not granted",
        _ => IsOpen ? "Click to close" : "Click to open",
    };

    public bool CanBrowse => _session is { HasShopScope: true };

    // ---- commands -------------------------------------------------------------------------------

    protected override void OnOpened()
    {
        SelectedCategory = null;
        Items = null;
        OnPropertyChanged(nameof(CardText));
        _ = LoadCategoriesAsync();
    }

    protected override void OnClosed()
    {
        CloseItem();
        CancelLoad();
        IsLoading = false;
        OnPropertyChanged(nameof(CardText));
    }

    [RelayCommand]
    private Task OpenCategory(ShopCategory category)
    {
        SelectedCategory = category;
        Items = null;
        return LoadItemsAsync(category);
    }

    [RelayCommand]
    private void Back()
    {
        CloseItem();
        CancelLoad();
        IsLoading = false;
        ErrorText = null;
        SelectedCategory = null;
        Items = null;
        if (Categories is null)
            _ = LoadCategoriesAsync();
    }

    [RelayCommand]
    private void SelectItem(ShopItemViewModel item)
    {
        if (IsBuying)
            return;
        BuyStatus = null;
        QuantityValue = 1;
        SelectedItem = item;
    }

    [RelayCommand]
    private void CloseItem()
    {
        if (IsBuying)
            return; // keep the card until the answer comes (the coins are being spent)
        BuyStatus = null;
        SelectedItem = null;
    }

    [RelayCommand]
    private void ChangeQuantity(string delta) =>
        QuantityValue = Math.Clamp(Quantity + int.Parse(delta), 1, MaxQuantity);

    /// <summary>
    /// Buys the item of the card (POST /v1/shop/buy, spends coins). The card itself is the
    /// confirmation: the player clicked the item, saw the price and clicked Buy. Never cancelled
    /// halfway; afterwards the items are reloaded (owned / can_buy changed) and the card stays open.
    /// </summary>
    [RelayCommand]
    private async Task BuyAsync()
    {
        if (!CanPressBuy || SelectedItem is not { } item || _session is not { } session)
            return;
        var qty = Quantity;
        IsBuying = true;
        BuyStatus = "Buying…";
        var r = await session.Session.BuyAsync(item.Item.Id, qty, CancellationToken.None);
        IsBuying = false;
        if (_session != session)
            return; // another client is shown now (Watch closed the card)

        var ok = r.StatusCode is >= 200 and < 300;
        BuyStatus = ok
            ? r.Value is { Spent: > 0 } v ? $"Bought (-{v.Spent:N0} coins)" : "Bought"
            : BuyErrorText(r.StatusCode, r.Error);
        if (ok && r.Value is { } bought)
            Coins = bought.Coins;
        if (ok && SelectedCategory is { } category)
            _ = LoadItemsAsync(category);

        var status = BuyStatus;
        await Task.Delay(BuyStatusTime);
        if (BuyStatus == status && !IsBuying)
            BuyStatus = null;
    }

    private static string BuyErrorText(int status, string? error) => (status, error) switch
    {
        (0, "not_connected") => "Not connected",
        (0, _) => "Game not responding",
        (403, "scope_required" or "addon_scope_revoked") => "No shop permission",
        (_, "funds_insufficient") => "Not enough coins",
        (504, _) => "Server timeout, try again",
        _ => $"Purchase failed ({error ?? status.ToString()})",
    };

    partial void OnCoinsChanged(long? value)
    {
        OnPropertyChanged(nameof(CanPressBuy));
        OnPropertyChanged(nameof(BuyText));
    }

    [RelayCommand]
    private Task Retry() => SelectedCategory is { } c ? LoadItemsAsync(c) : LoadCategoriesAsync();

    // ---- loading --------------------------------------------------------------------------------

    private async Task LoadCategoriesAsync()
    {
        if (BeginLoad() is not { } ct)
            return;
        var r = await _session!.Session.GetShopCategoriesAsync(ct);
        if (ct.IsCancellationRequested)
            return;
        IsLoading = false;
        if (r.IsSuccess)
            Categories = r.Value!.Categories;
        else
            ErrorText = Describe(r.StatusCode, r.Error);
    }

    private async Task LoadItemsAsync(ShopCategory category)
    {
        if (BeginLoad() is not { } ct)
            return;
        var r = await _session!.Session.GetShopItemsAsync(category.Id, ct);
        if (ct.IsCancellationRequested)
            return;
        IsLoading = false;
        if (!r.IsSuccess)
        {
            ErrorText = Describe(r.StatusCode, r.Error);
            return;
        }

        var shop = r.Value!;
        Coins = shop.Coins;
        var level = _session.Account?.Level;
        Items = shop.Items
            .Select(i => new ShopItemViewModel(i, shop.Coins, level))
            .ToList();

        // Reloaded after a purchase: the card shows the item's new state (owned, can_buy).
        if (SelectedItem is { } selected)
            SelectedItem = Items.FirstOrDefault(i => i.Item.Id == selected.Item.Id);
    }

    /// <summary>Cancels the previous load; returns null (and shows why) when there's nothing to load from.</summary>
    private CancellationToken? BeginLoad()
    {
        CancelLoad();
        ErrorText = null;
        if (!CanBrowse)
        {
            IsLoading = false;
            ErrorText = _session is { IsConnected: true }
                ? "The shop permission wasn't granted. Allow it in the game's popup (restart the game if you refused it)."
                : "No game client connected.";
            return null;
        }
        _loadCts = new CancellationTokenSource();
        IsLoading = true;
        return _loadCts.Token;
    }

    private void CancelLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private static string Describe(int status, string? error) => (status, error) switch
    {
        (403, "scope_required" or "addon_scope_revoked") => "The shop permission isn't available for this addon.",
        (0, "not_connected") => "No game client connected.",
        (0, _) => "The game didn't answer. Try again.",
        (504, _) => "The game servers didn't answer in time. Try again.",
        _ => $"Couldn't load the shop ({error ?? status.ToString()}).",
    };

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

        // Another account: its prices, discounts and owned items differ.
        Categories = null;
        Coins = null;
        IsBuying = false;
        if (IsOpen)
            Back();
        NotifyAccess();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.HasShopScope) or nameof(SessionViewModel.IsConnected))
        {
            NotifyAccess();
            // Permission just granted while the window shows the "not granted" message.
            if (IsOpen && CanBrowse && Categories is null && !IsLoading && SelectedCategory is null)
                _ = LoadCategoriesAsync();
        }
    }

    private void NotifyAccess()
    {
        OnPropertyChanged(nameof(CardText));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanPressBuy));
    }
}
