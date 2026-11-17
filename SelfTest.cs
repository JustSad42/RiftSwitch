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
