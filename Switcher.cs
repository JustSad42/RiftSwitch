using System.Windows.Threading;

namespace RiftSwitch;

enum ResultKind { Success, Info, Warning, Error }

sealed record SwitchResult(ResultKind Kind, string Title, string Message = "");

/// <summary>
/// Switches accounts and keeps saved sessions current. While the Riot Client runs, the session it writes is
/// copied back into the matching saved account every time it changes (the client rotates its tokens), and a
/// sign-in to an account RiftSwitch hasn't seen yet is saved as a new account.
/// </summary>
sealed class Switcher
{
    readonly AccountStore _store;
    readonly DispatcherTimer _timer;
    DateTime _lastWrite;
    DateTime _lastLeagueCheck;
    int _unsavedTicks;
    DateTime _streakCheck, _penaltyCheck;
    string? _infoPuuid;
    bool _ticking;

    public Switcher(AccountStore store)
    {
        _store = store;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public bool Busy { get; private set; }
    public string? ActiveId { get; private set; }

    /// <summary>Signed in without "Stay signed in", so this account can't be saved (Riot ID or empty).</summary>
    public bool NotPersistent { get; private set; }
    /// <summary>Waiting for the user to sign in to a new account in the Riot Client.</summary>
    public bool AddingAccount { get; private set; }

    public event Action? StateChanged;
    /// <summary>A short progress line while switching.</summary>
    public event Action<string>? Progress;
    /// <summary>Something worth telling the user (account saved, session expired…).</summary>
    public event Action<SwitchResult>? Notice;

    public void Start()
    {
        _timer.Start();
        _ = TickAsync();
    }

    void SetActive(string? id)
    {
        // A different account signed in: its next rank reading starts a new session (for "LP this session").
        if (id != null && id != ActiveId && _store.Find(id) is { } started)
        {
            started.SessionPending = true;
            _streakCheck = default;
        }
        ActiveId = id;
        foreach (var a in _store.Accounts) a.IsActive = a.Id == id;
    }

    // ---------- watching ----------

    internal async Task TickAsync()
    {
        if (_ticking || Busy) return;
        _ticking = true;
        try
        {
            CaptureIfChanged();

            var live = await Riot.QueryAsync();
            if (Busy) return;
            // "Stay signed in" is judged by the session file, not the client's persistLogin flag: that flag only
            // describes a fresh password sign-in and reads false after every restore from a saved session.
            // Only flagged after a few seconds, since the client writes the file just after signing in.
            bool unsaved = IsUnsaved(live, SessionData.InspectLive());
            _unsavedTicks = unsaved ? _unsavedTicks + 1 : 0;
            bool notPersistent = _unsavedTicks >= 3;
            string? active = live.Authenticated ? live.Puuid
                : live.Reachable ? null // client open on the sign-in screen
                : SessionData.InspectLive() is { HasSession: true } id ? id.Puuid : null;

            if (active != ActiveId || notPersistent != NotPersistent)
            {
                NotPersistent = notPersistent;
                SetActive(active);
                StateChanged?.Invoke();
            }

            if (DateTime.Now - _lastLeagueCheck > TimeSpan.FromSeconds(15))
            {
                _lastLeagueCheck = DateTime.Now;
                await UpdateLeagueInfoAsync();
            }
        }
        catch { /* try again next tick */ }
        finally { _ticking = false; }
    }

    /// <summary>Signed in, but the session file holds no saved sign-in for that account ("Stay signed in" not ticked).</summary>
    internal static bool IsUnsaved(LiveState live, SessionIdentity file) =>
        live.Authenticated && !(file.HasSession && file.Puuid == live.Puuid);

    /// <summary>Saves the live session into its account when the Riot Client has rewritten it.</summary>
    void CaptureIfChanged()
    {
        DateTime write;
        try { write = File.GetLastWriteTimeUtc(Riot.SessionFile); }
        catch { return; }
        if (write == _lastWrite || write.Year < 2000) return;
        _lastWrite = write;
        CaptureNow(announce: true);
    }

    string? _lastLogged;

    /// <summary>Copies the live session into its saved account. Returns the account, or null if there's no session.</summary>
    Account? CaptureNow(bool announce)
    {
        var who = SessionData.InspectLive();
        if (!who.HasSession || who.Puuid == null) return null;
        byte[] pack;
        try { pack = SessionData.Capture(); }
        catch { return null; }

        // The client may rewrite the file while it's being copied: only keep a copy that is still that account.
        var yaml = SessionData.YamlInPack(pack);
        if (yaml == null || SessionData.Inspect(yaml) is not { HasSession: true } copy || copy.Puuid != who.Puuid) return null;

        var (acc, isNew) = _store.Upsert(copy, pack);
        var print = SwitchLog.Describe(yaml);
        if (announce && print != _lastLogged) SwitchLog.Write($"monitor saved {(isNew ? "NEW " : "")}{print}");
        _lastLogged = print;
        if (announce && (isNew || AddingAccount))
        {
            AddingAccount = false;
            Notice?.Invoke(new SwitchResult(ResultKind.Success, isNew ? $"Added {acc.RiotId}" : $"Updated {acc.DisplayName}",
                isNew ? "Saved. You can switch to it any time." : "Its saved sign-in was refreshed."));
        }
        SetActive(acc.Id);
        StateChanged?.Invoke();
        return acc;
    }

    async Task UpdateLeagueInfoAsync()
    {
        var info = await Riot.QueryLeagueAsync();
        if (info?.Puuid == null || _store.Find(info.Puuid) is not { } acc) return;
        int gamesBefore = (acc.Solo?.Wins ?? 0) + (acc.Solo?.Losses ?? 0) + (acc.Flex?.Wins ?? 0) + (acc.Flex?.Losses ?? 0);
        acc.Solo = info.Solo ?? acc.Solo;
        acc.Flex = info.Flex ?? acc.Flex;
        acc.Level = info.Level ?? acc.Level;
        acc.RankUpdated = DateTime.Now;
        if (info.Solo != null) acc.RecordSolo(info.Solo);

        // A different account in League than last time: fetch everything right away.
        if (info.Puuid != _infoPuuid)
        {
            _infoPuuid = info.Puuid;
            _streakCheck = _penaltyCheck = default;
        }

        // Decay bank (Diamond+): stored as a date so the countdown keeps going while the account isn't played.
        if (info.DecayDays is { } days && acc.DecayApplies)
        {
            var at = DateTime.Now.AddDays(days);
            if (acc.DecayAt is not { } old || Math.Abs((old - at).TotalHours) > 1) acc.DecayAt = at;
            acc.DecayWarn = info.DecayWarning;
        }
        else if (info.Solo != null && !acc.DecayApplies) acc.DecayAt = null;

        // Recent games and streak: on sign-in, after a ranked game, and every few minutes (normals count too).
        int gamesNow = (acc.Solo?.Wins ?? 0) + (acc.Solo?.Losses ?? 0) + (acc.Flex?.Wins ?? 0) + (acc.Flex?.Losses ?? 0);
        if (gamesNow != gamesBefore || DateTime.Now - _streakCheck > TimeSpan.FromMinutes(3))
        {
            _streakCheck = DateTime.Now;
            if (await Riot.GetRecentGamesAsync(info.LockFile) is { } games)
            {
                foreach (var id in games.Take(5).Select(g => g.ChampionId).Distinct())
                    if (id > 0 && !ChampionIcons.Has(id) && await Riot.GetChampionIconAsync(info.LockFile, id) is { Length: > 0 } png)
                        ChampionIcons.Save(id, png);
                if (!games.Select(g => g.GameId).SequenceEqual(acc.Recent.Select(g => g.GameId))) acc.Recent = games;
                acc.Streak = Account.StreakOf(games);
            }
        }

        // Penalties: once a minute (timers run out on their own; the card counts down between checks).
        if (DateTime.Now - _penaltyCheck > TimeSpan.FromMinutes(1))
        {
            _penaltyCheck = DateTime.Now;
            var (known, penalty) = await Riot.GetPenaltyAsync(info.LockFile);
            if (known && (penalty?.Name != acc.Penalty || penalty?.Games != acc.PenaltyGames ||
                          (penalty?.Until is { } u && acc.PenaltyUntil is { } pu ? Math.Abs((u - pu).TotalMinutes) > 1 : penalty?.Until != acc.PenaltyUntil)))
                acc.ApplyPenalty(penalty);
        }
        if (info.IconId is { } icon && (icon != acc.IconId || !File.Exists(AccountStore.IconPath(acc.Id))))
        {
            if (await Riot.GetIconAsync(info.LockFile, icon) is { Length: > 0 } bytes)
            {
                AccountStore.SaveIcon(acc.Id, bytes);
                acc.IconId = icon;
                acc.ReloadAvatar();
            }
        }
        _store.Save();
    }

    // ---------- switching ----------

    public async Task<SwitchResult> SwitchAsync(Account target, bool launchLeague)
    {
        if (Busy) return new(ResultKind.Info, "Already switching", "Wait a moment and try again.");

        // Already on it: just make sure League is open.
        if (target.Id == ActiveId && !target.NeedsSignIn && Riot.IsRunning())
        {
            if (launchLeague)
            {
                SwitchLog.Begin($"Already on {target.RiotId}, opening League");
                // Riot's background mode (after Windows starts) has no Play button: wake the full client first.
                if (!Riot.FullClientRunning())
                {
                    SwitchLog.Step("Riot Client is in background mode, waking it");
                    try { Riot.Launch(league: true); } catch { }
                }
                _ = OpenLeagueAsync(target, announceNow: true);
            }
            return new(ResultKind.Info, $"Already on {target.DisplayName}", launchLeague ? "Opening League." : "");
        }

        if (await Riot.BlockReasonAsync() is { } reason) return new(ResultKind.Warning, "Can't switch right now", reason);

        Busy = true;
        target.IsBusy = true;
        AddingAccount = false;
        StateChanged?.Invoke();
        SwitchLog.Begin($"Switch to {target.RiotId} (acct {target.Id[..4]}), open League: {launchLeague}");
        try
        {
            Progress?.Invoke("Closing Riot Client…");
            await Riot.StopAsync();
            SwitchLog.Step("clients closed");

            Progress?.Invoke("Saving current sign-in…");
            var saved = CaptureNow(announce: false);
            SwitchLog.Step(saved != null ? $"saved current: {SwitchLog.Describe(SafeLiveYaml())}" : "nothing to save (signed out)");

            Progress?.Invoke($"Loading {target.DisplayName}…");
            var pack = _store.LoadSession(target);
            await Task.Run(() => SessionData.Restore(pack));
            try { _lastWrite = File.GetLastWriteTimeUtc(Riot.SessionFile); } catch { }
            SwitchLog.Step($"restored: {SwitchLog.Describe(SessionData.YamlInPack(pack))}");

            Progress?.Invoke("Starting Riot Client…");
            Riot.Launch(launchLeague);
            SwitchLog.Step("Riot Client started");
            target.LastUsed = DateTime.Now;
            target.RefreshTimes();
            _store.Save();
            SetActive(target.Id);
            StateChanged?.Invoke();

            Progress?.Invoke("Signing in…");
            var result = await WaitForSignInAsync(target);
            if (result.Kind != ResultKind.Success || !launchLeague) return result;

            Progress?.Invoke("Opening League…");
            bool started = await OpenLeagueAsync(target);
            return started ? result with { Message = "League is open." }
                : result with { Kind = ResultKind.Info, Message = "Press Play in the Riot Client to open League." };
        }
        catch (Exception ex)
        {
            SwitchLog.Step($"failed: {ex.Message}");
            return new(ResultKind.Error, "Switch failed", ex.Message);
        }
        finally
        {
            Busy = false;
            target.IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Raised when Riot has accepted Play and is preparing League (shows the "Starting League" indicator).</summary>
    public event Action<Account>? LeagueStarting;
    /// <summary>Raised when League's window process exists (true) or it never started (false).</summary>
    public event Action<Account, bool>? LeagueStarted;

    async Task<bool> OpenLeagueAsync(Account target, bool announceNow = false)
    {
        bool announced = false;
        if (announceNow)
        {
            // No busy screen on this path: show the "Starting League" pill straight away.
            announced = true;
            LeagueStarting?.Invoke(target);
        }
        // Up to 75 s for Riot to accept Play: its client sometimes spends 20-40 s fetching its settings first.
        bool ok = await Riot.PressPlayAsync(TimeSpan.FromSeconds(75), () =>
        {
            if (announced) return;
            announced = true;
            Progress?.Invoke("Riot is starting League (takes about 10 s)…");
            LeagueStarting?.Invoke(target);
        });
        if (!ok) SwitchLog.Step("League didn't start");
        if (announced) LeagueStarted?.Invoke(target, ok);
        return ok;
    }

    static string? SafeLiveYaml()
    {
        try { return SessionData.ReadShared(Riot.SessionFile); } catch { return null; }
    }

    /// <summary>
    /// Waits for the Riot Client to sign in with the restored session. Riot's client sometimes spends 30-40 s
    /// fetching its configuration before it even tries, so this waits up to two minutes. A session only counts as
    /// expired once the client throws it away (rewrites the session file without it), not just because the client
    /// isn't signed in yet.
    /// </summary>
    async Task<SwitchResult> WaitForSignInAsync(Account target)
    {
        var done = new SwitchResult(ResultKind.Success, $"Switched to {target.DisplayName}");
        if (Riot.TestMode) return done;

        var start = DateTime.Now;
        bool slowNoted = false;
        while (DateTime.Now - start < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(500);
            var live = await Riot.QueryAsync();
            if (live.Authenticated)
            {
                target.NeedsSignIn = false;
                SwitchLog.Step($"signed in as acct {live.Puuid?[..4]}");
                if (live.Puuid == target.Id) return done;
                SetActive(live.Puuid);
                return new(ResultKind.Warning, "Signed in to a different account",
                    "The Riot Client opened another account than the one picked. Sign in to the right one and it will be saved.");
            }

            // Rejected: the client cleared the restored session from its file and is showing its sign-in screen.
            if (!SessionData.InspectLive().HasSession)
            {
                SwitchLog.Step($"saved session rejected by Riot (client session state: {live.SessionType ?? "unknown"})");
                target.NeedsSignIn = true;
                _store.Save();
                SetActive(null);
                AddingAccount = true;
                return new(ResultKind.Warning, $"Sign in to {target.DisplayName} once",
                    "Riot didn't accept its saved sign-in. Sign in in the Riot Client with \"Stay signed in\" ticked, and RiftSwitch saves it again.");
            }

            if (!slowNoted && DateTime.Now - start > TimeSpan.FromSeconds(12))
            {
                slowNoted = true;
                Progress?.Invoke("Riot Client is slow to start, still waiting…");
                SwitchLog.Step("Riot Client still starting (slow start on Riot's side)");
            }
        }
        SwitchLog.Step("gave up waiting for sign-in after 2 min");
        return new(ResultKind.Info, $"Switched to {target.DisplayName}", "The Riot Client is taking a long time to sign in.");
    }

    /// <summary>Signs the Riot Client out (keeping the current account saved) so a new account can be signed in.</summary>
    public async Task<SwitchResult> BeginAddAccountAsync()
    {
        if (Busy) return new(ResultKind.Info, "Already switching", "Wait a moment and try again.");
        if (await Riot.BlockReasonAsync() is { } reason) return new(ResultKind.Warning, "Can't add an account right now", reason);

        Busy = true;
        StateChanged?.Invoke();
        try
        {
            Progress?.Invoke("Closing Riot Client…");
            await Riot.StopAsync();
            Progress?.Invoke("Saving current sign-in…");
            CaptureNow(announce: false);
            Progress?.Invoke("Opening the sign-in screen…");
            await Task.Run(SessionData.SignOut);
            try { _lastWrite = File.GetLastWriteTimeUtc(Riot.SessionFile); } catch { }
            Riot.Launch(false);
            SetActive(null);
            AddingAccount = true;
            return new(ResultKind.Info, "Sign in to your other account",
                "Use the Riot Client window. Tick \"Stay signed in\" and RiftSwitch saves the account automatically.");
        }
        catch (Exception ex)
        {
            return new(ResultKind.Error, "Couldn't open the sign-in screen", ex.Message);
        }
        finally
        {
            Busy = false;
            StateChanged?.Invoke();
        }
    }

    public void CancelAdd()
    {
        AddingAccount = false;
        StateChanged?.Invoke();
    }
}
