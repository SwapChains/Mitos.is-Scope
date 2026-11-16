using System.Text.Json;
using MitosScope.Core.Api;
using MitosScope.Core.Discovery;
using MitosScope.Core.Stats;

namespace MitosScope.Core.Sessions;

public enum SessionStatus
{
    Authenticating,
    /// <summary>The consent popup is open in the game (or waits for the player to leave the match).</summary>
    WaitingConsent,
    Connected,
    /// <summary>The player clicked "Deny". Stays like this until the game restarts.</summary>
    Denied,
    /// <summary>App key unknown or revoked, or consent removed (403 unknown_app / revoked).</summary>
    AppRejected,
    /// <summary>The client stopped answering (closed or logged out). Final state.</summary>
    Disconnected,
}

[Flags]
public enum SessionChange
{
    None = 0,
    Status = 1 << 0,
    Game = 1 << 1,
    Account = 1 << 2,
    Profile = 1 << 5,
    Quests = 1 << 7,
    /// <summary>MatchTracker: a match just ended (new last match).</summary>
    LastMatch = 1 << 8,
    BattlePass = 1 << 9,
    Wallet = 1 << 10,
    Equipment = 1 << 11,
    /// <summary><see cref="ClientSession.WindowHandle"/> became known.</summary>
    Window = 1 << 12,
    GemTraining = 1 << 13,
    All = Status | Game | Account | Profile | Quests | LastMatch | BattlePass | Wallet | Equipment | Window | GemTraining,
}

/// <summary>
/// One logged-in game client (one slot/port): authenticates, then polls /v1/game every second,
/// the uncached wallet and equipment every 15 s and on each death, the cached account sections every 60 s,
/// re-authenticating on 401. <see cref="RunAsync"/> ends when the client goes away.
/// </summary>
public sealed class ClientSession
{
    public static readonly TimeSpan GameInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SectionInterval = TimeSpan.FromSeconds(60);
    /// <summary>/v1/wallet and /v1/equipment aren't cached by the server (API v3), so they can be fresher.</summary>
    public static readonly TimeSpan LiveSectionInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);
    private const int MaxUnreachable = 3;

    /// <summary>
    /// Extended permissions asked in /v1/auth (API v3). "shop" is needed even just to browse the shop.
    /// If the player refuses it (or the key doesn't have it) the token still works for everything else.
    /// </summary>
    private static readonly string[] RequestedScopes = ["shop"];

    private readonly MitosApiClient _api;
    private readonly string _appKey;
    private string? _token; // never exposed or persisted (ADDON_API.md §8)

    public ClientSession(MitosApiClient api, HelloInfo hello, string appKey)
    {
        _api = api;
        _appKey = appKey;
        Hello = hello;
    }

    public HelloInfo Hello { get; }
    public int Port => Hello.Port;
    public int Slot => Hello.Slot;

    /// <summary>PID of the game process serving this port (null if unknown / not Windows).</summary>
    public int? ProcessId { get; internal set; }

    /// <summary>
    /// The client's main window (HWND from /v1/hwnd), fetched once after the first login. Null until
    /// then, on clients without /v1/hwnd, and outside Windows. Ties focus changes to this exact client.
    /// </summary>
    public long? WindowHandle { get; private set; }
    private bool _noHwnd;

    public SessionStatus Status { get; private set; } = SessionStatus.Authenticating;
    public string? LastError { get; private set; }

    /// <summary>While <see cref="SessionStatus.WaitingConsent"/>: which popup is open ("shop" for the shop one, null for the basic one).</summary>
    public string? PendingScope { get; private set; }

    /// <summary>The player allowed the shop permission (and our key has it). Changes raise <see cref="SessionChange.Status"/>.</summary>
    public bool HasShopScope { get; private set; }

    public GameState? Game { get; private set; }
    public AccountInfo? Account { get; private set; }
    public ProfileStats? Profile { get; private set; }
    public IReadOnlyList<Quest>? Quests { get; private set; }
    public BattlePassInfo? BattlePass { get; private set; }
    /// <summary>Null until the first /v1/wallet answer, or if the client doesn't have it (API before v3).</summary>
    public Wallet? Wallet { get; private set; }
    /// <summary>Null until the first /v1/equipment answer, or if the client doesn't have it (API before v3).</summary>
    public IReadOnlyList<EquipmentSlot>? Equipment { get; private set; }
    /// <summary>Null until the first /v1/gemtraining answer, or if the client doesn't have it (API before v4).</summary>
    public GemTraining? GemTraining { get; private set; }

    // Set when the client answers 404 (API before v3). A client's API version doesn't change while
    // it runs, so these are never reset.
    private bool _noWallet;
    private bool _noEquipment;
    private bool _noGemTraining;

    /// <summary>Live peak mass and last-match stats, fed by the game poll.</summary>
    public MatchTracker Matches { get; } = new();

    /// <summary>Raised on a background thread. Marshal to the UI thread before touching UI.</summary>
    public event Action<ClientSession, SessionChange>? Changed;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var status = await AuthenticateAsync(ct).ConfigureAwait(false);
                if (status == SessionStatus.Connected)
                {
                    await FetchWindowAsync(_token!, ct).ConfigureAwait(false);
                    var outcome = await PollAsync(_token!, ct).ConfigureAwait(false);
                    if (outcome == PollOutcome.Reauthenticate)
                        continue;
                    if (outcome == PollOutcome.Rejected)
                        status = SetStatus(SessionStatus.AppRejected);
                    else
                        break; // unreachable
                }

                if (status is SessionStatus.Denied or SessionStatus.AppRejected)
                    await WatchUntilGoneAsync(ct).ConfigureAwait(false); // don't insist, just wait for the client to close

                break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _token = null;
            SetStatus(SessionStatus.Disconnected);
        }
    }

    // ---- authentication -------------------------------------------------------------------

    private async Task<SessionStatus> AuthenticateAsync(CancellationToken ct)
    {
        SetStatus(SessionStatus.Authenticating);
        var unreachable = 0;

        while (true)
        {
            var r = await _api.AuthAsync(Port, _appKey, RequestedScopes, ct).ConfigureAwait(false);

            if (r.StatusCode == 200 && r.Value?.Token is { Length: > 0 } token)
            {
                _token = token;
                LastError = null;
                PendingScope = null;
                HasShopScope = r.Value.Scopes?.Contains("shop") == true;
                Raise(SessionChange.Status); // scopes may change even if the status doesn't
                return SetStatus(SessionStatus.Connected);
            }

            switch (r.StatusCode)
            {
                case 202:
                    unreachable = 0;
                    if (PendingScope != r.Value?.Scope)
                    {
                        PendingScope = r.Value?.Scope; // basic consent first, then the shop popup
                        Raise(SessionChange.Status);
                    }
                    SetStatus(SessionStatus.WaitingConsent);
                    await Task.Delay(AuthPollInterval, ct).ConfigureAwait(false);
                    continue;
                case 403 when r.Error == "denied":
                    LastError = r.Error;
                    return SetStatus(SessionStatus.Denied);
                case 403 or 400:
                    LastError = r.Error;
                    return SetStatus(SessionStatus.AppRejected);
                case 0:
                    LastError = r.Error;
                    if (++unreachable >= MaxUnreachable)
                        return SessionStatus.Disconnected;
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                default: // 5xx etc.: transient
                    LastError = r.Error;
                    await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    continue;
            }
        }
    }

    /// <summary>
    /// Asks the client for its window once: it doesn't change while the game runs. Any failure just
    /// leaves <see cref="WindowHandle"/> null (focus then falls back to the process id).
    /// </summary>
    private async Task FetchWindowAsync(string token, CancellationToken ct)
    {
        if (WindowHandle is not null || _noHwnd)
            return;

        var r = await _api.HwndAsync(Port, token, ct).ConfigureAwait(false);
        if (r.StatusCode == 404)
            _noHwnd = true; // older client
        if (r.Value is not { Hwnd: not 0 } info)
            return;

        WindowHandle = WindowOwner.RootOwner(info.Hwnd); // same form as the foreground watcher reports
        ProcessId ??= WindowOwner.FindProcessId(info.Hwnd); // when the port owner lookup failed
        Raise(SessionChange.Window);
    }

    private async Task WatchUntilGoneAsync(CancellationToken ct)
    {
        var unreachable = 0;
        while (unreachable < MaxUnreachable)
        {
            await Task.Delay(WatchInterval, ct).ConfigureAwait(false);
            var r = await _api.HelloAsync(Port, TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            unreachable = r.IsUnreachable ? unreachable + 1 : 0;
        }
    }

    // ---- polling --------------------------------------------------------------------------

    private enum PollOutcome { Reauthenticate, Rejected, Unreachable }

    private async Task<PollOutcome> PollAsync(string token, CancellationToken ct)
    {
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stop = new TaskCompletionSource<PollOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        var loops = Task.WhenAll(
            GameLoopAsync(token, stop, pollCts.Token),
            SectionsLoopAsync(token, stop, pollCts.Token),
            LiveSectionsLoopAsync(token, stop, pollCts.Token));

        try
        {
            return await stop.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await pollCts.CancelAsync().ConfigureAwait(false);
            try { await loops.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private async Task GameLoopAsync(string token, TaskCompletionSource<PollOutcome> stop, CancellationToken ct)
    {
        var unreachable = 0;
        using var timer = new PeriodicTimer(GameInterval);
        do
        {
            var r = await _api.GameAsync(Port, token, ct).ConfigureAwait(false);
            if (Handle(r, stop, ref unreachable))
            {
                // Death (alive true -> false): coins and durability just changed, refresh them now.
                if (Game?.Alive == true && !r.Value!.Alive)
                    RequestLiveRefresh();
                Game = r.Value;
                Raise(Matches.OnGame(r.Value!) ? SessionChange.Game | SessionChange.LastMatch : SessionChange.Game);
            }
        }
        while (!stop.Task.IsCompleted && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task SectionsLoopAsync(string token, TaskCompletionSource<PollOutcome> stop, CancellationToken ct)
    {
        _sectionsUnreachable = 0;
        while (!stop.Task.IsCompleted)
        {
            // Sequential on purpose: each one can take up to 20 s on the server side.
            await FetchSectionAsync<AccountInfo>("account", v => Account = v, SessionChange.Account, stop, token, ct);
            await FetchSectionAsync<List<Quest>>("quests", v => Quests = v, SessionChange.Quests, stop, token, ct);
            await FetchSectionAsync<ProfileStats>("profile", v => Profile = v, SessionChange.Profile, stop, token, ct);
            await FetchSectionAsync<BattlePassInfo>("battlepass", v => BattlePass = v, SessionChange.BattlePass, stop, token, ct);

            await Task.Delay(SectionInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task LiveSectionsLoopAsync(string token, TaskCompletionSource<PollOutcome> stop, CancellationToken ct)
    {
        while (!stop.Task.IsCompleted)
        {
            if (!_noWallet
                && await FetchSectionAsync<Wallet>("wallet", v => Wallet = v, SessionChange.Wallet, stop, token, ct) == 404)
                _noWallet = true;

            if (!_noEquipment
                && await FetchSectionAsync<List<EquipmentSlot>>("equipment", v => Equipment = v, SessionChange.Equipment, stop, token, ct) == 404)
                _noEquipment = true;

            if (!_noGemTraining
                && await FetchSectionAsync<GemTraining>("gemtraining", v => GemTraining = v, SessionChange.GemTraining, stop, token, ct) == 404)
                _noGemTraining = true;

            if (_noWallet && _noEquipment && _noGemTraining)
                return;
            // Every 15 s, or right away when the player dies (RequestLiveRefresh).
            await _liveRefresh.WaitAsync(LiveSectionInterval, ct).ConfigureAwait(false);
        }
    }

    private readonly SemaphoreSlim _liveRefresh = new(0, 1);

    /// <summary>Wakes the wallet/equipment loop early. Several requests before it runs count as one.</summary>
    private void RequestLiveRefresh()
    {
        try { _liveRefresh.Release(); }
        catch (SemaphoreFullException) { } // already requested
    }

    // Shared by both section loops: they hit the same port, so either one failing in a row counts.
    private int _sectionsUnreachable;

    /// <summary>Fetches one account section and applies it. Returns the HTTP status (0 = unreachable).</summary>
    private async Task<int> FetchSectionAsync<T>(string section, Action<T> apply, SessionChange change,
        TaskCompletionSource<PollOutcome> stop, string token, CancellationToken ct)
    {
        if (stop.Task.IsCompleted)
            return 0;
        var r = await _api.SectionAsync<T>(Port, section, token, ct).ConfigureAwait(false);
        if (r.StatusCode == 404)
            return 404; // endpoint missing in this client's API version: not an error for the session
        if (Handle(r, stop, ref _sectionsUnreachable))
        {
            apply(r.Value!.Data);
            Raise(change);
        }
        return r.StatusCode;
    }

    /// <summary>Returns true when the result carries data; otherwise decides whether polling must stop.</summary>
    private bool Handle<T>(ApiResult<T> r, TaskCompletionSource<PollOutcome> stop, ref int unreachable)
    {
        if (r.IsSuccess)
        {
            unreachable = 0;
            return true;
        }

        LastError = r.Error;
        switch (r.StatusCode)
        {
            case 401:
                stop.TrySetResult(PollOutcome.Reauthenticate);
                break;
            case 403:
                stop.TrySetResult(PollOutcome.Rejected);
                break;
            case 502 when r.Error == "addon_revoked":
                stop.TrySetResult(PollOutcome.Rejected);
                break;
            case 0:
                if (++unreachable >= MaxUnreachable)
                    stop.TrySetResult(PollOutcome.Unreachable);
                break;
            // 5xx / 504 / bad payload: keep the previous data and try again next tick.
        }
        return false;
    }

    // ---- on demand: inventory, and the shop (needs the "shop" permission) --------------------
    // Not routed through Handle(): a shop 403 (scope missing/revoked) must not end the session.

    /// <summary>
    /// Every item the account owns (/v1/inventory), for the inventory window. Not polled. The server
    /// caches it ~60 s, so right after a change its <c>equipped</c> flags can be stale: prefer
    /// <see cref="Equipment"/> (never cached) for what is equipped.
    /// </summary>
    public Task<ApiResult<SectionResponse<List<InventoryItem>>>> GetInventoryAsync(CancellationToken ct) =>
        OnDemandCallAsync((token, c) => _api.SectionAsync<List<InventoryItem>>(Port, "inventory", token, c), ct);

    public Task<ApiResult<ShopCategories>> GetShopCategoriesAsync(CancellationToken ct) =>
        OnDemandCallAsync((token, c) => _api.ShopAsync(Port, token, c), ct);

    public Task<ApiResult<ShopItems>> GetShopItemsAsync(int category, CancellationToken ct) =>
        OnDemandCallAsync((token, c) => _api.ShopItemsAsync(Port, category, token, c), ct);

    /// <summary>
    /// Buys a shop item (<paramref name="qty"/> purchases of a consumable). Spends coins. On success the
    /// coins are updated right away and wallet/equipment are re-fetched.
    /// </summary>
    public async Task<ApiResult<BuyResult>> BuyAsync(long itemId, int qty, CancellationToken ct)
    {
        var r = await OnDemandCallAsync((token, c) => _api.BuyAsync(Port, itemId, qty, token, c), ct).ConfigureAwait(false);
        if (r.StatusCode is >= 200 and < 300)
        {
            if (r.Value is { } v && Wallet is { } w)
            {
                Wallet = w with { Coins = v.Coins };
                Raise(SessionChange.Wallet);
            }
            RequestLiveRefresh(); // a bought consumable can be in a slot already (more uses)
        }
        return r;
    }

    /// <summary>
    /// Equips an owned item (<paramref name="slot"/> null = the server picks the slot), or unequips it.
    /// Not while alive in a match (409 "alive"). On success the equipped slots from the answer are
    /// applied right away, and wallet/equipment are re-fetched.
    /// </summary>
    public async Task<ApiResult<EquipResult>> EquipAsync(long itemId, int? slot, bool unequip, CancellationToken ct)
    {
        var r = await OnDemandCallAsync((token, c) => _api.EquipAsync(Port, itemId, slot, unequip, token, c), ct).ConfigureAwait(false);
        if (r.StatusCode is >= 200 and < 300)
        {
            if (r.Value?.Equip is { } equip)
            {
                Equipment = equip;
                Raise(SessionChange.Equipment);
            }
            RequestLiveRefresh();
        }
        return r;
    }

    /// <summary>Re-reads /v1/gemtraining now (the gem training window opens) instead of waiting for the 15 s poll.</summary>
    public async Task<ApiResult<SectionResponse<GemTraining>>> RefreshGemTrainingAsync(CancellationToken ct)
    {
        var r = await OnDemandCallAsync((token, c) => _api.SectionAsync<GemTraining>(Port, "gemtraining", token, c), ct).ConfigureAwait(false);
        if (r.IsSuccess && r.Value?.Data is { } data)
        {
            GemTraining = data;
            Raise(SessionChange.GemTraining);
        }
        return r;
    }

    /// <summary>
    /// Puts a gem in a training slot, or empties the slot when <paramref name="gemId"/> is null (needs
    /// the shop permission; slot 1 also equips the gem, so 409 "alive" in a match). The answer
    /// (the new gem training, plus the equipped slots after a change of slot 1) is applied right away.
    /// </summary>
    public async Task<ApiResult<GemTraining>> SetGemTrainingAsync(int slot, long? gemId, CancellationToken ct)
    {
        var r = await OnDemandCallAsync((token, c) => _api.GemTrainingAsync(Port, slot, gemId, token, c), ct).ConfigureAwait(false);
        if (r.StatusCode is not (>= 200 and < 300))
            return new ApiResult<GemTraining>(r.StatusCode, null, r.Error);

        GemTraining? data = null;
        try
        {
            var root = r.Value;
            // Same object as GET: maybe wrapped like the sections ({section, time, data}), maybe not.
            var body = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : root;
            data = body.Deserialize<GemTraining>(MitosJson.Options);
            if (data is { Equip: null } && root.TryGetProperty("equip", out var e) && e.ValueKind == JsonValueKind.Array)
                data = data with { Equip = e.Deserialize<List<EquipmentSlot>>(MitosJson.Options) };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

        if (data is { Enabled: true })
        {
            GemTraining = data with { Equip = null };
            Raise(SessionChange.GemTraining);
        }
        if (data?.Equip is { } equip)
        {
            Equipment = equip;
            Raise(SessionChange.Equipment);
        }
        if (data is null)
            await RefreshGemTrainingAsync(ct).ConfigureAwait(false);
        RequestLiveRefresh();
        return new ApiResult<GemTraining>(r.StatusCode, data, null);
    }

    /// <summary>
    /// Repairs one equipped item, or all the broken equipment when <paramref name="itemId"/> is null.
    /// Spends coins. On success the coins are updated right away and wallet/equipment are re-fetched.
    /// </summary>
    public async Task<ApiResult<RepairResult>> RepairAsync(long? itemId, CancellationToken ct)
    {
        var r = await OnDemandCallAsync((token, c) => _api.RepairAsync(Port, itemId, token, c), ct).ConfigureAwait(false);
        if (r.StatusCode is >= 200 and < 300)
        {
            if (r.Value is { } v && Wallet is { } w)
            {
                Wallet = w with { Coins = v.Coins };
                Raise(SessionChange.Wallet);
            }
            RequestLiveRefresh(); // new durability and repair prices
        }
        return r;
    }

    private async Task<ApiResult<T>> OnDemandCallAsync<T>(Func<string, CancellationToken, Task<ApiResult<T>>> call, CancellationToken ct)
    {
        if (Status != SessionStatus.Connected || _token is not { } token)
            return new ApiResult<T>(0, default, "not_connected");

        var r = await call(token, ct).ConfigureAwait(false);
        if (r.StatusCode == 403 && r.Error is "scope_required" or "addon_scope_revoked" && HasShopScope)
        {
            HasShopScope = false;
            Raise(SessionChange.Status);
        }
        return r;
    }

    // ---- helpers --------------------------------------------------------------------------

    private SessionStatus SetStatus(SessionStatus status)
    {
        if (Status != status)
        {
            Status = status;
            Raise(SessionChange.Status);
        }
        return status;
    }

    private void Raise(SessionChange change) => Changed?.Invoke(this, change);

    public override string ToString() => $"Slot {Slot} (port {Port}) {Status}";
}
