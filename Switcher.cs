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
            bool notPersistent = live.Authenticated && !live.PersistLogin;
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
        acc.Solo = info.Solo ?? acc.Solo;
        acc.Flex = info.Flex ?? acc.Flex;
        acc.Level = info.Level ?? acc.Level;
        acc.RankUpdated = DateTime.Now;
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

        // Already on it: just make sure the client (and League) is open.
        if (target.Id == ActiveId && !target.NeedsSignIn && Riot.IsRunning())
        {
            if (launchLeague) try { Riot.Launch(true); } catch { }
            return new(ResultKind.Info, $"Already on {target.DisplayName}", launchLeague ? "Opening League." : "");
        }

        if (await Riot.BlockReasonAsync() is { } reason) return new(ResultKind.Warning, "Can't switch right now", reason);

        Busy = true;
        target.IsBusy = true;
        AddingAccount = false;
        StateChanged?.Invoke();
        try
        {
            Progress?.Invoke("Closing Riot Client…");
            await Riot.StopAsync();

            Progress?.Invoke("Saving current sign-in…");
            CaptureNow(announce: false);

            Progress?.Invoke($"Loading {target.DisplayName}…");
            var pack = _store.LoadSession(target);
            await Task.Run(() => SessionData.Restore(pack));
            try { _lastWrite = File.GetLastWriteTimeUtc(Riot.SessionFile); } catch { }

            Progress?.Invoke(launchLeague ? "Starting League…" : "Starting Riot Client…");
            Riot.Launch(launchLeague);
            target.LastUsed = DateTime.Now;
            target.RefreshTimes();
            _store.Save();
            SetActive(target.Id);
            StateChanged?.Invoke();

            Progress?.Invoke("Signing in…");
            return await WaitForSignInAsync(target, launchLeague);
        }
        catch (Exception ex)
        {
            return new(ResultKind.Error, "Switch failed", ex.Message);
        }
        finally
        {
            Busy = false;
            target.IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    async Task<SwitchResult> WaitForSignInAsync(Account target, bool league)
    {
        var done = new SwitchResult(ResultKind.Success, $"Switched to {target.DisplayName}", league ? "League is starting." : "");
        if (Riot.TestMode) return done;

        DateTime start = DateTime.Now, reachableSince = default;
        while (DateTime.Now - start < TimeSpan.FromSeconds(45))
        {
            await Task.Delay(600);
            var live = await Riot.QueryAsync();
            if (!live.Reachable) continue;
            if (reachableSince == default) reachableSince = DateTime.Now;

            if (live.Authenticated)
            {
                target.NeedsSignIn = false;
                if (live.Puuid == target.Id) return done;
                SetActive(live.Puuid);
                return new(ResultKind.Warning, "Signed in to a different account",
                    "The Riot Client opened another account than the one picked. Sign in to the right one and it will be saved.");
            }
            // The client opened on its sign-in screen: the saved session has expired or was signed out.
            if (live.SessionType is not null and not "authenticated" && DateTime.Now - reachableSince > TimeSpan.FromSeconds(5))
            {
                target.NeedsSignIn = true;
                _store.Save();
                SetActive(null);
                AddingAccount = true;
                return new(ResultKind.Warning, $"Sign in to {target.DisplayName} once",
                    "Its saved sign-in expired. Sign in in the Riot Client with \"Stay signed in\" ticked, and RiftSwitch saves it again.");
            }
        }
        return new(ResultKind.Info, $"Switched to {target.DisplayName}", "The Riot Client is taking a while to start.");
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
