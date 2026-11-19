#if DEBUG
using System.Text;

namespace RiftSwitch;

/// <summary>
/// Debug-only end-to-end test of switching, run with --selftest against a fake Riot Client folder
/// (RIFTSWITCH_RIOT_DIR, RIFTSWITCH_RIOT_EXE, RIFTSWITCH_DATA must point at scratch locations).
/// </summary>
static class SelfTest
{
    static readonly List<string> Log = [];
    static int _failed;

    static void Check(bool ok, string what)
    {
        Log.Add((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) _failed++;
    }

    static string Jwt(string sub, string name, string tag, string cpid)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = $"{{\"sub\":\"{sub}\",\"acct\":{{\"game_name\":\"{name}\",\"tag_line\":\"{tag}\",\"state\":\"ENABLED\"}}," +
                      $"\"lol_region\":[{{\"cpid\":\"{cpid}\",\"active\":true}}],\"exp\":9999999999}}";
        return B64("{\"alg\":\"RS256\"}") + "." + B64(payload) + ".sig";
    }

    static string Yaml(string sub, string name, string tag, string cpid, string refresh) => $"""
        psl:
            authorization:
                riot-client:
                    claims: null
                    id_token: "{Jwt(sub, name, tag, cpid)}"
                    is_dpop_bound: false
                    refresh_token: "{refresh}"
                    refresh_token_write_count: 1
        rso-authenticator:
            tdid:
                domain: "riotgames.com"
                name: "tdid"
                value: "DEVICE-COOKIE-123"
        """.Replace("\r\n", "\n");

    static void WriteLive(string yaml)
    {
        Directory.CreateDirectory(Riot.DataDir);
        File.WriteAllText(Riot.SessionFile, yaml);
        // Make sure the change is seen even within the file system's timestamp resolution.
        File.SetLastWriteTimeUtc(Riot.SessionFile, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 100000)));
    }

    static string Live() => File.ReadAllText(Riot.SessionFile);
    static string Saved(AccountStore s, Account a) => SessionData.YamlInPack(s.LoadSession(a)) ?? "";

    public static async Task<int> RunAsync()
    {
        if (!Riot.TestMode || Environment.GetEnvironmentVariable("RIFTSWITCH_DATA") == null)
        {
            Console.WriteLine("Refusing to run: test folders aren't set.");
            return 2;
        }
        const string A = "aaaaaaaa-1111-4111-8111-aaaaaaaaaaaa", B = "bbbbbbbb-2222-4222-8222-bbbbbbbbbbbb";

        var store = new AccountStore();
        store.Load();
        var sw = new Switcher(store);
        var notices = new List<SwitchResult>();
        sw.Notice += notices.Add;

        // 1. A is signed in with "Stay signed in": first tick saves it as a new account.
        WriteLive(Yaml(A, "MainGuy", "EUW", "EUW1", "refreshA1-xxxxxxxxxxxxxxxxxxxxxxxx"));
        File.WriteAllText(Path.Combine(Riot.DataDir, "extra.dat"), "A-extra");
        await sw.TickAsync();
        var a = store.Find(A);
        Check(a != null, "signed-in account is saved on first look");
        Check(a?.RiotId == "MainGuy#EUW" && a.Region == "EUW", "Riot ID and region read from the token");
        Check(sw.ActiveId == A && a?.IsActive == true, "account shown as signed in");
        Check(notices.Any(n => n.Title.Contains("Added")), "user told the account was added");

        // 2. The client rotates the refresh token: the saved copy follows.
        WriteLive(Yaml(A, "MainGuy", "EUW", "EUW1", "refreshA2-xxxxxxxxxxxxxxxxxxxxxxxx"));
        await sw.TickAsync();
        Check(Saved(store, a!).Contains("refreshA2"), "rotated token saved into the account");

        // 3. Add account: client signed out, device cookie kept.
        var add = await sw.BeginAddAccountAsync();
        Check(add.Kind == ResultKind.Info && sw.AddingAccount, "add account opens the sign-in screen");
        Check(!Live().Contains("refresh_token") && Live().Contains("DEVICE-COOKIE-123"), "signed out but device cookie kept");
        Check(!File.Exists(Path.Combine(Riot.DataDir, "extra.dat")), "other session files cleared on sign-out");
        Check(Saved(store, a!).Contains("refreshA2"), "A still saved after sign-out");

        // 4. The user signs in to B: saved automatically.
        notices.Clear();
        WriteLive(Yaml(B, "SmurfGal", "1234", "NA1", "refreshB1-xxxxxxxxxxxxxxxxxxxxxxxx"));
        await sw.TickAsync();
        var b = store.Find(B);
        Check(b != null && store.Accounts.Count == 2, "second account saved after sign-in");
        Check(!sw.AddingAccount && notices.Any(n => n.Title.Contains("SmurfGal")), "add finished and announced");

        // 5. Switch to A: live folder becomes A's saved session, B is kept.
        var r = await sw.SwitchAsync(a!, launchLeague: true);
        Check(r.Kind == ResultKind.Success, $"switch to A succeeds ({r.Title} {r.Message})");
        Check(Live().Contains("refreshA2") && SessionData.InspectLive().Puuid == A, "live session is A's");
        Check(File.Exists(Path.Combine(Riot.DataDir, "extra.dat")) &&
              File.ReadAllText(Path.Combine(Riot.DataDir, "extra.dat")) == "A-extra", "A's other files restored");
        Check(Saved(store, b!).Contains("refreshB1"), "B's session kept");
        Check(sw.ActiveId == A && a!.LastUsed != null, "A marked active and used");
        await Task.Delay(400);
        Check(Riot.IsRunning(), "client launched after switching");

        // 6. A's token rotates while running; switching to B saves A's newest token first.
        WriteLive(Yaml(A, "MainGuy", "EUW", "EUW1", "refreshA3-xxxxxxxxxxxxxxxxxxxxxxxx"));
        r = await sw.SwitchAsync(b!, launchLeague: false);
        Check(r.Kind == ResultKind.Success, "switch to B succeeds");
        Check(Saved(store, a!).Contains("refreshA3"), "A's latest token saved before switching away");
        Check(Live().Contains("refreshB1") && SessionData.InspectLive().Puuid == B, "live session is B's");
        Check(!File.Exists(Path.Combine(Riot.DataDir, "extra.dat")), "A's extra file not left behind for B");

        // 7. Persistence: a fresh store reads both accounts back, in order, with sessions decryptable.
        var store2 = new AccountStore();
        store2.Load();
        Check(store2.Accounts.Count == 2 && store2.Accounts[0].Id == A && store2.Accounts[1].Id == B, "accounts reload in order");
        Check(Saved(store2, store2.Accounts[0]).Contains("refreshA3"), "saved sessions decrypt after reload");
        var raw = File.ReadAllBytes(Path.Combine(AccountStore.Dir, "sessions", A + ".dat"));
        Check(!Encoding.UTF8.GetString(raw).Contains("refreshA3"), "session file on disk is encrypted");

        // 8. A sign-in without "Stay signed in" (no refresh token) is not saved.
        WriteLive(Yaml("cccccccc-3333-4333-8333-cccccccccccc", "NoPersist", "X", "EUW1", "null"));
        await sw.TickAsync();
        Check(store.Accounts.Count == 2, "session without refresh token isn't saved");

        // 8b. The "can't be saved" banner: based on the session file, not the client's persistLogin flag.
        var signedInA = new LiveState { Reachable = true, SessionType = "authenticated", Puuid = A };
        var fileA = SessionData.Inspect(Yaml(A, "MainGuy", "EUW", "EUW1", "refreshA9-xxxxxxxxxxxxxxxxxxxxxxxx"));
        var fileNone = SessionData.Inspect(Yaml(A, "MainGuy", "EUW", "EUW1", "null"));
        var fileB = SessionData.Inspect(Yaml(B, "SmurfGal", "1234", "NA1", "refreshB9-xxxxxxxxxxxxxxxxxxxxxxxx"));
        Check(!Switcher.IsUnsaved(signedInA, fileA), "no banner when the signed-in account's session is saved");
        Check(Switcher.IsUnsaved(signedInA, fileNone), "banner when signed in without a saved session");
        Check(Switcher.IsUnsaved(signedInA, fileB), "banner when the file holds a different account");
        Check(!Switcher.IsUnsaved(new LiveState { Reachable = true, SessionType = "needs_credentials" }, fileNone), "no banner on the sign-in screen");

        // 8c. LP this session, rank history, tags, reorder.
        var acc = new Account { Id = "dddddddd-4444-4444-8444-dddddddddddd", GameName = "Climber", TagLine = "EUW", Platform = "EUW1" };
        acc.RecordSolo(new Rank { Tier = "GOLD", Division = "I", Lp = 80 });
        Check(acc.SessionStart?.Value == 3*400 + 3*100 + 80 && acc.LpDelta == null, "first reading starts the session, no delta yet");
        acc.Solo = new Rank { Tier = "PLATINUM", Division = "IV", Lp = 15 };
        acc.RecordSolo(acc.Solo);
        Check(acc.LpDelta == 35, $"LP delta counts across a promotion (+{acc.LpDelta})");
        acc.RecordSolo(acc.Solo);
        Check(acc.RankHistory.Count == 2 && acc.HasSpark, "history only grows when the rank changes; sparkline shown");
        acc.SessionPending = true;
        acc.RecordSolo(acc.Solo);
        Check(acc.SessionStart!.Tier == "PLATINUM" && acc.LpDelta == null, "new session resets the baseline");
        acc.Solo = new Rank { Tier = "GOLD", Division = "I", Lp = 90 };
        acc.RecordSolo(acc.Solo);
        Check(acc.LpDelta == -25 && acc.LpDeltaText.StartsWith("−25 LP"), "losses show as negative");
        Check(RankPoint.ValueOf("MASTER", "I", 120) == 2920 && RankPoint.ValueOf("", "", 0) == null, "apex and unranked values");
        Check(acc.OpGg == "https://op.gg/lol/summoners/euw/Climber-EUW" && acc.UGg.Contains("/euw1/Climber-EUW/"), "profile links");
        acc.Streak = 3;
        Check(acc.HasStreak && acc.StreakText == "3W streak" && acc.StreakIsWin, "win streak badge");
        acc.Streak = -1;
        Check(!acc.HasStreak, "a single game isn't shown as a streak");
        acc.SavedAt = DateTime.Now.AddDays(-30);
        Check(acc.IsStale && acc.AgeText.Contains("30 days"), "old saved sign-ins are flagged");

        store.Accounts[0].Tags = ["Main", "EUW"];
        store.Accounts[1].Tags = ["smurf", "euw"];
        Check(store.AllTags().SequenceEqual(new[] { "EUW", "Main", "smurf" }), "tag list is merged case-insensitively");
        var firstId = store.Accounts[0].Id;
        store.MoveTo(store.Accounts[0], 1);
        Check(store.Accounts[1].Id == firstId && store.Accounts[1].Slot == 2, "drag reorder moves and renumbers");
        store.Save();
        var store3 = new AccountStore();
        store3.Load();
        Check(store3.Accounts[1].Id == firstId && store3.Accounts[1].Tags.SequenceEqual(new[] { "Main", "EUW" }), "order and tags are saved");

        // 8d. Crash safety: a list zeroed by a power cut falls back to the backup; with both gone, sessions rebuild it.
        var listPath = Path.Combine(AccountStore.Dir, "accounts.json");
        File.WriteAllBytes(listPath, new byte[2048]);
        var store4 = new AccountStore();
        store4.Load();
        Check(store4.UsedBackup && store4.Accounts.Count == 2, "damaged list falls back to the backup");
        Check(Directory.GetFiles(AccountStore.Dir, "accounts.json.broken-*").Length > 0, "damaged list is kept aside, not overwritten");
        File.WriteAllBytes(listPath, new byte[2048]);
        File.Delete(listPath + ".bak");
        var store5 = new AccountStore();
        store5.Load();
        Check(store5.Recovered == 2 && store5.Find(A)?.RiotId == "MainGuy#EUW" && store5.Find(B)?.RiotId == "SmurfGal#1234",
              "accounts rebuilt from saved sessions when list and backup are both lost");
        Check(Saved(store5, store5.Find(A)!).Contains("refreshA3"), "rebuilt account keeps its saved sign-in");
        var store6 = new AccountStore();
        store6.Load();
        Check(store6.Recovered == 0 && !store6.UsedBackup && store6.Accounts.Count == 2, "rebuilt list is saved");

        // 8e. Recent games: customs/Practice Tool skipped, remakes marked, newest first, streak ignores remakes.
        static string Game(long id, string type, string mode, int queue, int secs, long created, int champ, bool win, int place = 0) =>
            $"{{\"gameId\":{id},\"gameType\":\"{type}\",\"gameMode\":\"{mode}\",\"queueId\":{queue},\"gameDuration\":{secs},\"gameCreation\":{created}," +
            $"\"participants\":[{{\"championId\":{champ},\"stats\":{{\"win\":{(win ? "true" : "false")},\"kills\":7,\"deaths\":2,\"assists\":9,\"subteamPlacement\":{place}}}}}]}}";
        long t0 = DateTimeOffset.Now.AddHours(-1).ToUnixTimeMilliseconds();
        var gamesJson = "[" + string.Join(",",
            Game(1, "CUSTOM_GAME", "PRACTICETOOL", 3140, 3527, t0, 114, false),
            Game(2, "MATCHED_GAME", "CLASSIC", 420, 1800, t0 - 3_000_000, 103, true),
            Game(3, "MATCHED_GAME", "CLASSIC", 420, 200, t0 - 6_000_000, 103, false),
            Game(4, "MATCHED_GAME", "CLASSIC", 420, 1700, t0 - 9_000_000, 64, true),
            Game(5, "MATCHED_GAME", "CHERRY", 1700, 1400, t0 - 12_000_000, 64, false, 2),
            Game(6, "MATCHED_GAME", "ARAM", 450, 1200, t0 - 15_000_000, 22, false)) + "]";
        using (var gd = System.Text.Json.JsonDocument.Parse(gamesJson))
        {
            var recent = Riot.ParseGames(gd.RootElement, new Dictionary<int, string> { [103] = "Ahri", [64] = "Lee Sin" });
            Check(recent.Count == 5 && recent[0].GameId == 2, "Practice Tool left out, newest first");
            Check(recent[0].Champion == "Ahri" && recent[0].Mode == "Ranked Solo" && recent[0].Tip.Contains("7/2/9"), "game details read");
            Check(recent[1].Remake && recent[3].Result == "2nd place" && recent[3].Mode == "Arena", "remakes and Arena placement");
            Check(Account.StreakOf(recent) == 2, $"streak skips remakes ({Account.StreakOf(recent)})");
            var shown = new Account { Recent = recent };
            Check(shown.LastFive.Count == 5 && shown.ShowRecent, "last five shown on the card");
            CardOptions.Recent = false;
            Check(!shown.ShowRecent && shown.HasRecent, "recent games can be turned off in settings");
            CardOptions.Recent = true;
        }

        // 8f. Penalties from League's queue check.
        long until = DateTimeOffset.Now.AddMinutes(25).ToUnixTimeMilliseconds();
        var elig = $$$"""
            [{"queueId":430,"eligible":false,"restrictions":[{"restrictionCode":"QueueDisabled","expiredTimestamp":0,"restrictionArgs":{}}]},
             {"queueId":400,"eligible":false,"restrictions":[{"restrictionCode":"PlayerDodgeRestriction","expiredTimestamp":0,"restrictionArgs":{}}]},
             {"queueId":420,"eligible":false,"restrictions":[{"restrictionCode":"PlayerLeaverQueueLockoutRestriction","expiredTimestamp":{{{until}}},"restrictionArgs":{}}]}]
            """;
        using (var ed = System.Text.Json.JsonDocument.Parse(elig))
        {
            var p = Riot.ParsePenalty(ed.RootElement);
            Check(p?.Name == "Queue lockout" && p.Until > DateTime.Now.AddMinutes(20), "most serious penalty picked, with its end time");
            var pa = new Account();
            pa.ApplyPenalty(p);
            Check(pa.HasPenalty && pa.PenaltyText.StartsWith("Queue lockout · 25 min"), $"penalty badge text ({pa.PenaltyText})");
            pa.PenaltyUntil = DateTime.Now.AddMinutes(-1);
            Check(!pa.HasPenalty, "expired penalty disappears on its own");
        }
        using (var ok = System.Text.Json.JsonDocument.Parse("""[{"queueId":420,"eligible":true,"restrictions":[]},{"queueId":430,"eligible":false,"restrictions":[{"restrictionCode":"QueueDisabled"}]}]"""))
            Check(Riot.ParsePenalty(ok.RootElement) == null, "disabled queues aren't penalties");
        using (var gb = System.Text.Json.JsonDocument.Parse("""[{"queueId":420,"restrictions":[{"restrictionCode":"PlayerGameBasedRankRestriction","restrictionArgs":{"gamesRemaining":"4"}}]}]"""))
        {
            var pa = new Account();
            pa.ApplyPenalty(Riot.ParsePenalty(gb.RootElement));
            Check(pa.PenaltyText == "Ranked restricted · 4 games", $"games-based restriction ({pa.PenaltyText})");
        }

        // 8g. Decay countdown (Diamond+ only).
        var dec = new Account { Solo = new Rank { Tier = "MASTER", Lp = 824 }, DecayAt = DateTime.Now.AddDays(13) };
        Check(dec.HasDecay && dec.DecayText == "Decay in 13 days" && !dec.DecayUrgent, $"decay countdown ({dec.DecayText})");
        dec.DecayAt = DateTime.Now.AddDays(2);
        Check(dec.DecayUrgent, "decay warns in the last 3 days");
        dec.Solo = new Rank { Tier = "EMERALD", Division = "I", Lp = 50 };
        Check(!dec.HasDecay, "no decay shown below Diamond");

        // 9. Stale lockfile from a crashed client is ignored (owner process check).
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Riot.DataDir)!, "Config"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Riot.DataDir)!, "Config", "lockfile"), $"Riot Client:{Environment.ProcessId}:1:pw:https");
        var live = await Riot.QueryAsync();
        Check(!live.Reachable, "lockfile owned by another process is ignored");

        await Riot.StopAsync();
        Check(!Riot.IsRunning(), "fake client closed");

        Log.Add(_failed == 0 ? $"ALL {Log.Count} CHECKS PASSED" : $"{_failed} OF {Log.Count} CHECKS FAILED");
        File.WriteAllLines(Path.Combine(AccountStore.Dir, "selftest.txt"), Log);
        return _failed == 0 ? 0 : 1;
    }
}
#endif
