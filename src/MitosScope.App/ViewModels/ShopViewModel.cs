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
/// First page: the categories; clicking one shows its items in the same window. Read-only for now
/// (no buying). Everything is loaded when shown, never polled. Use on the UI thread.
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
        CancelLoad();
        IsLoading = false;
        ErrorText = null;
        SelectedCategory = null;
        Items = null;
        if (Categories is null)
            _ = LoadCategoriesAsync();
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
    }
}
