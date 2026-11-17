using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace RiftSwitch;

/// <summary>What the running Riot Client reports about its sign-in.</summary>
sealed class LiveState
{
    /// <summary>The Riot Client's local API answered.</summary>
    public bool Reachable;
    /// <summary>authenticated, needs_credentials, needs_multifactor_verification…</summary>
    public string? SessionType;
    public string? Puuid;
    public bool Authenticated => SessionType == "authenticated" && Puuid != null;
}

/// <summary>Finds, queries, stops and starts the Riot Client and the League client.</summary>
static class Riot
{
    // Test hooks: point the app at a fake Riot Client so testing never touches the real one.
    static readonly string? TestDir = Environment.GetEnvironmentVariable("RIFTSWITCH_RIOT_DIR");
    static readonly string? TestExe = Environment.GetEnvironmentVariable("RIFTSWITCH_RIOT_EXE");
    public static bool TestMode => TestDir != null;

    static string LocalRoot => TestDir ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client");

    /// <summary>The folder where a "Stay signed in" session lives (RiotGamesPrivateSettings.yaml).</summary>
    public static string DataDir => Path.Combine(LocalRoot, "Data");
    public static string SessionFile => Path.Combine(DataDir, "RiotGamesPrivateSettings.yaml");
    static string LockFile => Path.Combine(LocalRoot, "Config", "lockfile");

    static readonly string InstallsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");

    // Everything that keeps the signed-in session in memory or the Data folder open.
    static readonly string[] ClientProcesses =
    [
        "LeagueClientUxRender", "LeagueClientUx", "LeagueClient", "LeagueCrashHandler64", "LeagueCrashHandler",
        "Riot Client", "RiotClientUxRender", "RiotClientUx", "RiotClientServices", "RiotClientCrashHandler",
    ];

    // A match in progress. Never closed by the switcher.
    static readonly (string Process, string Name)[] GameProcesses =
    [
        ("League of Legends", "League of Legends"),
        ("VALORANT-Win64-Shipping", "VALORANT"),
        ("LoR", "Legends of Runeterra"),
    ];

    // Closing the League client in these phases costs a dodge or a leave.
    static readonly string[] CommittedPhases =
        ["Matchmaking", "ReadyCheck", "ChampSelect", "GameStart", "InProgress", "Reconnect", "CheckedIntoTournament"];

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        // Both clients serve their local API over HTTPS with a self-signed certificate. Requests carry the
        // lockfile password, so the certificate check is only skipped for this machine.
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.IsLoopback == true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    // ---------- install locations ----------

    public static string? ClientExe()
    {
        if (TestMode) return TestExe;
        var installs = ReadInstalls();
        foreach (var key in new[] { "rc_default", "rc_live" })
            if (installs?.TryGetProperty(key, out var v) == true && v.GetString() is { } exe && File.Exists(exe)) return exe;
        const string fallback = @"C:\Riot Games\Riot Client\RiotClientServices.exe";
        return File.Exists(fallback) ? fallback : null;
    }

    /// <summary>League install folders (each has a lockfile while the League client runs).</summary>
    public static List<string> LeagueDirs()
    {
        var dirs = new List<string>();
        if (TestMode) return dirs;
        var installs = ReadInstalls();
        if (installs?.TryGetProperty("associated_client", out var map) == true && map.ValueKind == JsonValueKind.Object)
            foreach (var p in map.EnumerateObject())
                if (p.Name.Contains("League of Legends", StringComparison.OrdinalIgnoreCase)) dirs.Add(p.Name);
        if (dirs.Count == 0 && Directory.Exists(@"C:\Riot Games\League of Legends")) dirs.Add(@"C:\Riot Games\League of Legends");
        return dirs;
    }

    static JsonElement? ReadInstalls()
    {
        try { return JsonDocument.Parse(File.ReadAllText(InstallsFile)).RootElement.Clone(); }
        catch { return null; }
    }

    // ---------- processes ----------

    public static bool IsRunning() => TestMode ? TestProcesses().Count > 0 : ClientProcesses.Any(n => Count(n) > 0);

    /// <summary>A sentence explaining why the clients must not be closed right now, or null.</summary>
    public static async Task<string?> BlockReasonAsync()
    {
        if (TestMode) return null;
        foreach (var (proc, name) in GameProcesses)
            if (Count(proc) > 0) return $"{name} is running. Finish your game before switching accounts.";

        foreach (var dir in LeagueDirs())
        {
            using var doc = await GetAsync(Path.Combine(dir, "lockfile"), "LeagueClient", "/lol-gameflow/v1/gameflow-phase");
            if (doc?.RootElement.ValueKind == JsonValueKind.String && doc.RootElement.GetString() is { } phase &&
                CommittedPhases.Contains(phase))
                return phase switch
                {
                    "Matchmaking" => "You're in queue. Leave the queue first, or switching would pull you out of it.",
                    "ReadyCheck" => "You're in a ready check. Switching now would decline it.",
                    "ChampSelect" => "You're in champ select. Switching now would count as a dodge.",
                    _ => "League says you're in a game. Finish it before switching accounts.",
                };
        }
        return null;
    }

    /// <summary>Closes the League client and the Riot Client and returns once every process is gone.</summary>
    public static async Task StopAsync()
    {
        if (!IsRunning()) return;

        // Closed straight away: the Riot Client writes a refreshed session to disk the moment it gets one,
        // so there's nothing to flush, and a polite quit only adds 1-3 seconds.
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var running = TestMode ? TestProcesses() : ClientProcesses.SelectMany(Process.GetProcessesByName).ToList();
            if (running.Count == 0) break;
            foreach (var p in running)
            {
                try { p.Kill(); }
                catch { /* exiting already, or elevated: the timeout reports it */ }
                finally { p.Dispose(); }
            }
            if (clock.Elapsed > TimeSpan.FromSeconds(20))
                throw new IOException("The Riot Client could not be closed. Close it from the system tray and try again.");
            await Task.Delay(200);
        }
        await Task.Delay(100); // file handles of exited processes can linger for a moment (restore also retries)
    }

    // Pressing Play blocks the request (and the client's whole API) while Riot prepares the launch, ~10-14 s.
    static readonly HttpClient LaunchHttp = CreateLaunchHttp();
    static HttpClient CreateLaunchHttp()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (req, _, _, _) => req.RequestUri?.IsLoopback == true,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
    }

    static bool LeagueRunning() => Count("LeagueClientUx") > 0 || Count("LeagueClient") > 0;

    /// <summary>
    /// Presses Play for League in the running Riot Client. Riot's client no longer starts League on its own
    /// after signing in (its log says "Auto-launch is disabled"), so without this it waits for a click.
    /// <paramref name="starting"/> is called once Riot has accepted the press and is preparing League (Riot's
    /// own telemetry puts that at 10-14 s, the same as a manual click). Returns true once League's process exists.
    /// </summary>
    public static async Task<bool> PressPlayAsync(TimeSpan timeout, Action? starting = null)
    {
        if (TestMode) { starting?.Invoke(); return true; }
        const string product = "/product-launcher/v1/products/league_of_legends/patchlines/live";
        var clock = Stopwatch.StartNew();
        string? lastRefusal = null;
        bool accepted = false;

        while (clock.Elapsed < timeout && !accepted)
        {
            if (LeagueRunning()) { starting?.Invoke(); return true; }
            if (ReadLock(LockFile, "RiotClientServices") is not { } l) { await Task.Delay(300); continue; }

            // Asked straight away: the client checks eligibility itself and answers 464 until the account's
            // agreements have loaded (about a second after sign-in). Refusals come back in well under a second.
            var post = Task.Run(async () =>
            {
                using var req = Request(HttpMethod.Post, l, product);
                using var res = await LaunchHttp.SendAsync(req);
                return (int)res.StatusCode;
            });
            if (await Task.WhenAny(post, Task.Delay(1500)) != post)
            {
                // Still waiting after 1.5 s: the client took the press and is preparing League.
                accepted = true;
                SwitchLog.Step("Play accepted, Riot is preparing League");
                starting?.Invoke();
                break;
            }
            int code;
            try { code = await post; }
            catch { await Task.Delay(300); continue; }

            if (code is >= 200 and < 300 or 423) // 423 = already launching or launched
            {
                accepted = true;
                SwitchLog.Step($"Play accepted (HTTP {code})");
                starting?.Invoke();
                break;
            }
            if ($"{code}" != lastRefusal) SwitchLog.Step($"Play not ready yet (HTTP {code}), retrying");
            lastRefusal = $"{code}";
            await Task.Delay(250);
        }
        if (!accepted) return false;

        // One press is enough: wait for League's process instead of pressing again.
        while (clock.Elapsed < timeout + TimeSpan.FromSeconds(30))
        {
            if (LeagueRunning())
            {
                SwitchLog.Step("League started");
                return true;
            }
            await Task.Delay(250);
        }
        return false;
    }

    static List<Process> TestProcesses()
    {
        if (TestExe == null) return [];
        var name = Path.GetFileNameWithoutExtension(TestExe);
        return Process.GetProcessesByName(name).ToList();
    }

    /// <summary>Starts the Riot Client, which signs in with the stored session (and opens League if asked).</summary>
    public static void Launch(bool league)
    {
        var exe = ClientExe() ?? throw new FileNotFoundException("The Riot Client wasn't found. Is it installed?");
        var args = league ? "--launch-product=league_of_legends --launch-patchline=live" : "";
        using var _ = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe),
        });
    }

    static int Count(string name)
    {
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length;
    }

    // ---------- local APIs ----------

    public static async Task<LiveState> QueryAsync()
    {
        var state = new LiveState();
        using var session = await GetAsync(LockFile, "RiotClientServices", "/rso-auth/v1/session");
        if (session == null) return state;
        state.Reachable = true;
        var s = session.RootElement;
        state.SessionType = Str(s, "type");
        if (state.SessionType != "authenticated") return state;

        using var auth = await GetAsync(LockFile, "RiotClientServices", "/rso-auth/v1/authorization");
        if (auth != null) state.Puuid = Str(auth.RootElement, "subject")?.ToLowerInvariant();
        return state;
    }

    /// <summary>Signed-in player's level, icon and ranks, from the League client (null when it isn't running).</summary>
    public static async Task<LeagueInfo?> QueryLeagueAsync()
    {
        foreach (var dir in LeagueDirs())
        {
            var lockFile = Path.Combine(dir, "lockfile");
            using var me = await GetAsync(lockFile, "LeagueClient", "/lol-summoner/v1/current-summoner");
            if (me == null) continue;
            var r = me.RootElement;
            var info = new LeagueInfo
            {
                Puuid = Str(r, "puuid")?.ToLowerInvariant(),
                Level = r.TryGetProperty("summonerLevel", out var lv) && lv.TryGetInt32(out var l) ? l : null,
                IconId = r.TryGetProperty("profileIconId", out var ic) && ic.TryGetInt32(out var i) ? i : null,
                LockFile = lockFile,
            };
            using var ranked = await GetAsync(lockFile, "LeagueClient", "/lol-ranked/v1/current-ranked-stats");
            if (ranked?.RootElement.TryGetProperty("queueMap", out var q) == true)
            {
                info.Solo = ReadRank(q, "RANKED_SOLO_5x5");
                info.Flex = ReadRank(q, "RANKED_FLEX_SR");
            }
            return info;
        }
        return null;
    }

    static Rank? ReadRank(JsonElement queueMap, string queue)
    {
        if (!queueMap.TryGetProperty(queue, out var e)) return null;
        var tier = Str(e, "tier") ?? "";
        if (tier is "" or "NONE") return new Rank { Tier = "" };
        return new Rank
        {
            Tier = tier,
            Division = Str(e, "division") is { } d && d != "NA" ? d : "",
            Lp = e.TryGetProperty("leaguePoints", out var lp) && lp.TryGetInt32(out var x) ? x : 0,
            Wins = e.TryGetProperty("wins", out var w) && w.TryGetInt32(out var wi) ? wi : 0,
            Losses = e.TryGetProperty("losses", out var lo) && lo.TryGetInt32(out var li) ? li : 0,
        };
    }

    /// <summary>Downloads a profile icon from the running League client.</summary>
    public static async Task<byte[]?> GetIconAsync(string lockFile, int iconId)
    {
        if (ReadLock(lockFile, "LeagueClient") is not { } l) return null;
        try
        {
            using var req = Request(HttpMethod.Get, l, $"/lol-game-data/assets/v1/profile-icons/{iconId}.jpg");
            using var res = await Http.SendAsync(req);
            return res.IsSuccessStatusCode ? await res.Content.ReadAsByteArrayAsync() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Current win/loss streak of the signed-in player from the League client's recent games:
    /// positive = wins in a row, negative = losses in a row, null if unknown. Remakes (under 5 min) are skipped.
    /// </summary>
    public static async Task<int?> GetStreakAsync(string lockFile)
    {
        using var doc = await GetAsync(lockFile, "LeagueClient",
            "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=19");
        if (doc == null || !doc.RootElement.TryGetProperty("games", out var outer) ||
            !outer.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array) return null;

        var results = new List<(long When, bool Win)>();
        foreach (var g in games.EnumerateArray())
        {
            if (g.TryGetProperty("gameDuration", out var d) && d.TryGetInt32(out var secs) && secs < 300) continue;
            if (!g.TryGetProperty("participants", out var ps) || ps.ValueKind != JsonValueKind.Array || ps.GetArrayLength() == 0) continue;
            if (!ps[0].TryGetProperty("stats", out var stats) || !stats.TryGetProperty("win", out var win)) continue;
            long when = g.TryGetProperty("gameCreation", out var c) && c.TryGetInt64(out var ms) ? ms : 0;
            results.Add((when, win.ValueKind == JsonValueKind.True));
        }
        if (results.Count == 0) return null;
        results.Sort((a, b) => b.When.CompareTo(a.When)); // newest first
        bool first = results[0].Win;
        int n = results.TakeWhile(r => r.Win == first).Count();
        return first ? n : -n;
    }

    record Lock(int Port, string Password);

    /// <summary>Reads a lockfile (name:pid:port:password:protocol) whose owner process is still the right one.</summary>
    static Lock? ReadLock(string lockFile, string processName)
    {
        string[] parts;
        try
        {
            using var fs = new FileStream(lockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            parts = reader.ReadToEnd().Split(':');
        }
        catch { return null; }
        if (parts.Length < 5 || !int.TryParse(parts[1], out var pid) || !int.TryParse(parts[2], out var port)) return null;

        // A lockfile can outlive a crashed client; its port may belong to something else by now.
        try
        {
            using var owner = Process.GetProcessById(pid);
            if (!owner.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        return new Lock(port, parts[3]);
    }

    static HttpRequestMessage Request(HttpMethod method, Lock l, string path)
    {
        var req = new HttpRequestMessage(method, $"https://127.0.0.1:{l.Port}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + l.Password)));
        return req;
    }

    static async Task<JsonDocument?> GetAsync(string lockFile, string processName, string path)
    {
        if (ReadLock(lockFile, processName) is not { } l) return null;
        try
        {
            using var req = Request(HttpMethod.Get, l, path);
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

sealed class LeagueInfo
{
    public string? Puuid;
    public int? Level, IconId;
    public Rank? Solo, Flex;
    public string LockFile = "";
}
