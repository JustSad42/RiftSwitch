using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RiftSwitch;

sealed class Rank
{
    public string Tier { get; set; } = "";       // IRON … CHALLENGER, "" = unranked
    public string Division { get; set; } = "";   // I-IV, "" for apex tiers
    public int Lp { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }

    [JsonIgnore] public bool IsRanked => Tier != "";
    [JsonIgnore] bool Apex => Tier is "MASTER" or "GRANDMASTER" or "CHALLENGER";
    [JsonIgnore] public string TierName => IsRanked ? char.ToUpper(Tier[0]) + Tier[1..].ToLower() : "Unranked";
    [JsonIgnore] public string Short => !IsRanked ? "Unranked" : Apex ? $"{TierName} · {Lp} LP" : $"{TierName} {Division} · {Lp} LP";
    [JsonIgnore] public string LpLine => !IsRanked ? "" : Apex ? $"{Lp} LP" : $"{Division} · {Lp} LP";
    [JsonIgnore] public string Record
    {
        get
        {
            int games = Wins + Losses;
            return games == 0 ? "" : $"{Wins}W {Losses}L · {Math.Round(100.0 * Wins / games)}%";
        }
    }

    /// <summary>Position on the ladder in LP (Iron IV 0 LP = 0, each division 100, Master+ = 2800 + LP).</summary>
    [JsonIgnore] public int? Value => RankPoint.ValueOf(Tier, Division, Lp);
}

/// <summary>Which extras the account cards show (from the settings).</summary>
static class CardOptions
{
    public static bool Recent = true, Decay = true, Penalty = true, Streak = true, LpDelta = true, Spark = true, Age = true;
}

/// <summary>One recent game, shown as a champion icon with a win/loss bar.</summary>
sealed class RecentGame : INotifyPropertyChanged
{
    public long GameId { get; set; }
    public int ChampionId { get; set; }
    public string Champion { get; set; } = "";
    public bool Win { get; set; }
    public bool Remake { get; set; }
    /// <summary>Arena placement (1-8), 0 elsewhere.</summary>
    public int Placement { get; set; }
    public DateTime At { get; set; }
    public int Minutes { get; set; }
    public string Mode { get; set; } = "";
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }

    [JsonIgnore] public ImageSource? Icon => ChampionIcons.Get(ChampionId);
    [JsonIgnore] public string Result => Remake ? "Remake" : Placement > 0 ? Ordinal(Placement) + " place" : Win ? "Win" : "Loss";
    [JsonIgnore]
    public string Tip
    {
        get
        {
            var ago = DateTime.Now - At;
            var when = ago.TotalHours < 1 ? $"{Math.Max(1, (int)ago.TotalMinutes)} min ago" : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago"
                : ago.TotalDays < 2 ? "yesterday" : $"{(int)ago.TotalDays} days ago";
            return $"{(Champion != "" ? Champion : "Champion " + ChampionId)} · {Result}\n{Mode} · {Kills}/{Deaths}/{Assists} · {Minutes} min\n{when}";
        }
    }
    static string Ordinal(int n) => n + (n == 1 ? "st" : n == 2 ? "nd" : n == 3 ? "rd" : "th");

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tip)));
}

/// <summary>Champion icons cached in icons\champions (downloaded from the League client once).</summary>
static class ChampionIcons
{
    static readonly Dictionary<int, ImageSource?> Cache = [];
    public static string Dir => Path.Combine(AccountStore.Dir, "icons", "champions");
    public static string PathOf(int id) => Path.Combine(Dir, id + ".png");
    public static bool Has(int id) => File.Exists(PathOf(id));

    public static ImageSource? Get(int id)
    {
        if (Cache.TryGetValue(id, out var img) && img != null) return img;
        img = null;
        if (Has(id))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(PathOf(id));
                bmp.DecodePixelWidth = 44;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { }
        }
        Cache[id] = img;
        return img;
    }

    public static void Save(int id, byte[] png)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(PathOf(id), png);
        Cache.Remove(id);
    }
}

/// <summary>A queue penalty read from the League client.</summary>
sealed record PenaltyInfo(string Name, string Detail, bool Severe, DateTime? Until = null, int? Games = null);

/// <summary>A solo-queue rank at a moment in time (history and session start).</summary>
sealed class RankPoint
{
    public DateTime At { get; set; }
    public string Tier { get; set; } = "";
    public string Division { get; set; } = "";
    public int Lp { get; set; }

    static readonly string[] Tiers = ["IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND"];
    static readonly string[] Divisions = ["IV", "III", "II", "I"];

    public static int? ValueOf(string tier, string division, int lp)
    {
        if (tier is "MASTER" or "GRANDMASTER" or "CHALLENGER") return 2800 + lp;
        int t = Array.IndexOf(Tiers, tier), d = Array.IndexOf(Divisions, division);
        return t < 0 || d < 0 ? null : t * 400 + d * 100 + lp;
    }

    [JsonIgnore] public int? Value => ValueOf(Tier, Division, Lp);
    public static RankPoint From(Rank r) => new() { At = DateTime.Now, Tier = r.Tier, Division = r.Division, Lp = r.Lp };
    [JsonIgnore] public string Name => Tier == "" ? "Unranked"
        : (char.ToUpper(Tier[0]) + Tier[1..].ToLower() + (Division != "" && Tier is not ("MASTER" or "GRANDMASTER" or "CHALLENGER") ? " " + Division : "")) + $" {Lp} LP";
}

sealed class Account : INotifyPropertyChanged
{
    public string Id { get; set; } = "";          // PUUID
    public string GameName { get; set; } = "";
    public string TagLine { get; set; } = "";
    public string Platform { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public DateTime? LastUsed { get; set; }
    public int Order { get; set; }

    string _label = "", _note = "", _color = "";
    public string Label { get => _label; set { _label = value; Changed(); Changed(nameof(DisplayName)); Changed(nameof(Initial)); Changed(nameof(ShowRiotId)); } }
    public string Note { get => _note; set { _note = value; Changed(); Changed(nameof(HasNote)); } }
    public string Color { get => _color; set { _color = value; Changed(); Changed(nameof(ColorBrush)); Changed(nameof(ColorSoft)); } }

    bool _needsSignIn;
    public bool NeedsSignIn { get => _needsSignIn; set { _needsSignIn = value; Changed(); } }

    Rank? _solo, _flex;
    int? _level;
    public Rank? Solo { get => _solo; set { _solo = value; Changed(); Changed(nameof(HasRank)); Changed(nameof(SoloBrush)); DecayChanged(); } }
    public Rank? Flex { get => _flex; set { _flex = value; Changed(); Changed(nameof(HasFlex)); } }
    public int? Level { get => _level; set { _level = value; Changed(); } }
    public int? IconId { get; set; }
    public DateTime? RankUpdated { get; set; }

    // ---------- tags, history, session ----------

    List<string> _tags = [];
    public List<string> Tags { get => _tags; set { _tags = value ?? []; Changed(); Changed(nameof(HasTags)); } }
    [JsonIgnore] public bool HasTags => Tags.Count > 0;

    /// <summary>Solo-queue rank each time it changed (newest last).</summary>
    public List<RankPoint> RankHistory { get; set; } = [];
    /// <summary>Solo rank when the current (or last) session on this account started.</summary>
    public RankPoint? SessionStart { get; set; }
    /// <summary>Set when the account becomes active; the next rank reading becomes the session start.</summary>
    public bool SessionPending { get; set; }

    int? _streak;
    /// <summary>Wins (positive) or losses (negative) in a row.</summary>
    public int? Streak { get => _streak; set { _streak = value; Changed(); Changed(nameof(StreakText)); Changed(nameof(HasStreak)); Changed(nameof(StreakIsWin)); Changed(nameof(ShowStreak)); } }
    [JsonIgnore] public bool HasStreak => Streak is { } s && Math.Abs(s) >= 2;
    [JsonIgnore] public bool StreakIsWin => Streak > 0;
    [JsonIgnore] public bool ShowStreak => CardOptions.Streak && HasStreak;
    [JsonIgnore] public string StreakText => Streak is { } s ? (s > 0 ? $"{s}W streak" : $"{-s}L streak") : "";

    // ---------- recent games ----------

    List<RecentGame> _recent = [];
    /// <summary>Last matched games (newest first; customs and Practice Tool left out).</summary>
    public List<RecentGame> Recent { get => _recent; set { _recent = value ?? []; Changed(); Changed(nameof(LastFive)); Changed(nameof(HasRecent)); Changed(nameof(ShowRecent)); } }
    [JsonIgnore] public List<RecentGame> LastFive => Recent.Take(5).ToList();
    [JsonIgnore] public bool HasRecent => Recent.Count > 0;
    [JsonIgnore] public bool ShowRecent => CardOptions.Recent && HasRecent;

    /// <summary>Wins (positive) or losses (negative) in a row, newest first, remakes skipped.</summary>
    public static int? StreakOf(IEnumerable<RecentGame> newestFirst)
    {
        var games = newestFirst.Where(g => !g.Remake).ToList();
        if (games.Count == 0) return null;
        bool first = games[0].Win;
        int n = games.TakeWhile(g => g.Win == first).Count();
        return first ? n : -n;
    }

    // ---------- rank decay (Diamond and above) ----------

    DateTime? _decayAt;
    bool _decayWarn;
    /// <summary>When solo rank starts decaying if no ranked games are played.</summary>
    public DateTime? DecayAt { get => _decayAt; set { _decayAt = value; DecayChanged(); } }
    /// <summary>League itself is showing its decay warning.</summary>
    public bool DecayWarn { get => _decayWarn; set { _decayWarn = value; DecayChanged(); } }
    [JsonIgnore] public bool DecayApplies => Solo?.Tier is "DIAMOND" or "MASTER" or "GRANDMASTER" or "CHALLENGER";
    [JsonIgnore] public int? DecayDays => DecayAt is { } t && DecayApplies ? Math.Max(0, (int)Math.Ceiling((t - DateTime.Now).TotalDays)) : null;
    [JsonIgnore] public bool HasDecay => DecayDays != null;
    [JsonIgnore] public bool DecayUrgent => DecayDays <= 3 || (HasDecay && DecayWarn);
    [JsonIgnore] public bool ShowDecay => CardOptions.Decay && HasDecay;
    [JsonIgnore] public bool ShowDecayUrgent => CardOptions.Decay && DecayUrgent;
    [JsonIgnore] public string DecayText => DecayDays switch { null => "", 0 => "Decaying", 1 => "Decay in 1 day", var d => $"Decay in {d} days" };
    [JsonIgnore] public string DecayTip => DecayDays == 0
        ? "This account is losing LP to inactivity. Play a ranked solo game to stop it."
        : $"Solo rank starts decaying on {DecayAt:ddd d MMM} unless you play ranked solo. Each game adds days back.";
    void DecayChanged()
    {
        foreach (var n in new[] { nameof(DecayDays), nameof(HasDecay), nameof(DecayUrgent), nameof(ShowDecay), nameof(ShowDecayUrgent), nameof(DecayText), nameof(DecayTip) }) Changed(n);
    }

    // ---------- penalties ----------

    string? _penalty;
    /// <summary>Queue lockout, low priority queue, ranked restriction, ban… (null = none).</summary>
    public string? Penalty { get => _penalty; set { _penalty = value; PenaltyChanged(); } }
    public string PenaltyDetail { get; set; } = "";
    public DateTime? PenaltyUntil { get; set; }
    public int? PenaltyGames { get; set; }
    /// <summary>False for warnings that don't stop you from queueing (leaver warning, playtime limit).</summary>
    public bool PenaltySevere { get; set; }

    [JsonIgnore] public bool HasPenalty => Penalty != null && (PenaltyUntil == null || PenaltyUntil > DateTime.Now);
    [JsonIgnore] public bool ShowPenalty => CardOptions.Penalty && HasPenalty;
    [JsonIgnore]
    public string PenaltyText => !HasPenalty ? ""
        : PenaltyUntil is { } t ? $"{Penalty} · {Left(t - DateTime.Now)}"
        : PenaltyGames is { } g ? $"{Penalty} · {g} game{(g == 1 ? "" : "s")}"
        : Penalty!;
    [JsonIgnore]
    public string PenaltyTip => !HasPenalty ? "" : PenaltyDetail +
        (PenaltyUntil is { } u ? $"\nEnds {u:ddd d MMM, HH:mm}." : "") + "\nChecked while League was open on this account.";

    public void ApplyPenalty(PenaltyInfo? p)
    {
        PenaltyDetail = p?.Detail ?? "";
        PenaltyUntil = p?.Until;
        PenaltyGames = p?.Games;
        PenaltySevere = p?.Severe ?? false;
        Penalty = p?.Name;
    }
    void PenaltyChanged()
    {
        foreach (var n in new[] { nameof(HasPenalty), nameof(ShowPenalty), nameof(PenaltyText), nameof(PenaltyTip), nameof(PenaltySevere) }) Changed(n);
    }
    static string Left(TimeSpan s) => s.TotalMinutes < 60 ? $"{Math.Max(1, (int)Math.Ceiling(s.TotalMinutes))} min left"
        : s.TotalHours < 48 ? $"{(int)Math.Ceiling(s.TotalHours)} h left" : $"{(int)Math.Ceiling(s.TotalDays)} days left";

    /// <summary>LP gained or lost since the session started (across promotions).</summary>
    [JsonIgnore]
    public int? LpDelta => SessionStart?.Value is { } from && Solo?.Value is { } now && now != from ? now - from : null;
    [JsonIgnore] public bool HasLpDelta => LpDelta != null;
    [JsonIgnore] public bool ShowLpDelta => CardOptions.LpDelta && HasLpDelta;
    [JsonIgnore] public bool LpDeltaUp => LpDelta > 0;
    [JsonIgnore] public string LpDeltaText => LpDelta is { } d ? $"{(d > 0 ? "+" : "−")}{Math.Abs(d)} LP {(IsActive ? "this session" : "last session")}" : "";

    /// <summary>Line points for the LP history sparkline (96 × 22).</summary>
    [JsonIgnore]
    public PointCollection? Spark
    {
        get
        {
            var vals = RankHistory.Select(p => p.Value).Where(v => v != null).Select(v => v!.Value).TakeLast(30).ToList();
            if (vals.Count < 2) return null;
            double min = vals.Min(), max = vals.Max(), range = Math.Max(40, max - min);
            var pts = new PointCollection();
            for (int i = 0; i < vals.Count; i++)
                pts.Add(new System.Windows.Point(i * 96.0 / (vals.Count - 1), 20 - (vals[i] - min) / range * 18 + 1));
            pts.Freeze();
            return pts;
        }
    }
    [JsonIgnore] public bool HasSpark => Spark != null;
    [JsonIgnore] public bool ShowSpark => CardOptions.Spark && HasSpark;
    [JsonIgnore] public bool ShowAge => CardOptions.Age && SavedAt != default;
    [JsonIgnore]
    public string SparkTip
    {
        get
        {
            var ranked = RankHistory.Where(p => p.Value != null).ToList();
            if (ranked.Count < 2) return "";
            var peak = ranked.MaxBy(p => p.Value)!;
            return $"LP history since {ranked[0].At:d MMM}: {ranked[0].Name} → {ranked[^1].Name}\nPeak: {peak.Name} ({peak.At:d MMM})";
        }
    }

    /// <summary>Records a new solo rank: history point if it changed, session start if one is pending.</summary>
    public void RecordSolo(Rank r)
    {
        if (!r.IsRanked) return;
        var point = RankPoint.From(r);
        var last = RankHistory.LastOrDefault();
        if (last == null || last.Value != point.Value)
        {
            RankHistory.Add(point);
            if (RankHistory.Count > 300) RankHistory.RemoveRange(0, RankHistory.Count - 300);
        }
        if (SessionPending || SessionStart == null)
        {
            SessionStart = point;
            SessionPending = false;
        }
        Changed(nameof(Spark)); Changed(nameof(HasSpark)); Changed(nameof(ShowSpark)); Changed(nameof(SparkTip));
        Changed(nameof(LpDelta)); Changed(nameof(HasLpDelta)); Changed(nameof(ShowLpDelta)); Changed(nameof(LpDeltaUp)); Changed(nameof(LpDeltaText));
    }

    /// <summary>How long ago the saved sign-in was last refreshed by the Riot Client.</summary>
    [JsonIgnore]
    public string AgeText
    {
        get
        {
            if (SavedAt == default) return "";
            var ago = DateTime.Now - SavedAt;
            var text = ago.TotalMinutes < 2 ? "just now" : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago"
                : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago" : ago.TotalDays < 2 ? "yesterday" : $"{(int)ago.TotalDays} days ago";
            return $"Sign-in saved {text}";
        }
    }
    /// <summary>Not refreshed for 3 weeks: Riot may ask for the password again.</summary>
    [JsonIgnore] public bool IsStale => SavedAt != default && DateTime.Now - SavedAt > TimeSpan.FromDays(21);
    [JsonIgnore] public string AgeTip => IsStale
        ? "This saved sign-in hasn't been used in a while, so Riot may ask for the password again. Switching to it refreshes it."
        : "Refreshed every time you play on this account.";

    // ---------- profile links ----------

    string NameTag => Uri.EscapeDataString($"{GameName}-{TagLine}");
    [JsonIgnore] public string OpGg => $"https://op.gg/lol/summoners/{Region.ToLowerInvariant()}/{NameTag}";
    [JsonIgnore] public string UGg => $"https://u.gg/lol/profile/{Platform.ToLowerInvariant()}/{NameTag}/overview";
    [JsonIgnore] public string LeagueOfGraphs => $"https://www.leagueofgraphs.com/summoner/{Region.ToLowerInvariant()}/{NameTag}";

    // ---------- display ----------

    bool _active;
    [JsonIgnore] public bool IsActive { get => _active; set { _active = value; Changed(); Changed(nameof(LpDeltaText)); } }
    bool _dragging;
    [JsonIgnore] public bool IsDragging { get => _dragging; set { _dragging = value; Changed(); } }
    bool _busy;
    [JsonIgnore] public bool IsBusy { get => _busy; set { _busy = value; Changed(); } }
    int _slot;
    /// <summary>1-9 number shown in the quick switcher.</summary>
    [JsonIgnore] public int Slot { get => _slot; set { _slot = value; Changed(); Changed(nameof(SlotText)); } }
    [JsonIgnore] public string SlotText => _slot is >= 1 and <= 9 ? _slot.ToString() : "";

    [JsonIgnore] public string RiotId => TagLine == "" ? GameName : $"{GameName}#{TagLine}";
    [JsonIgnore] public string DisplayName => Label != "" ? Label : GameName != "" ? GameName : "Unknown account";
    [JsonIgnore] public bool ShowRiotId => Label != "" && GameName != "";
    [JsonIgnore] public string Initial => DisplayName.Length > 0 ? char.ToUpper(DisplayName[0]).ToString() : "?";
    [JsonIgnore] public bool HasNote => Note != "";
    [JsonIgnore] public bool HasRank => Solo != null;
    [JsonIgnore] public bool HasFlex => Flex?.IsRanked == true;
    [JsonIgnore] public string Region => RegionName(Platform);
    [JsonIgnore] public string LevelText => Level is { } l ? $"Lv {l}" : "";
    [JsonIgnore] public Brush SoloBrush => TierBrush(Solo?.Tier ?? "");

    [JsonIgnore]
    public Brush ColorBrush
    {
        get
        {
            try { return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(Color != "" ? Color : Palette[Math.Abs(Id.GetHashCode()) % Palette.Length])); }
            catch { return Brushes.Gray; }
        }
    }
    [JsonIgnore]
    public Brush ColorSoft
    {
        get
        {
            var c = ((SolidColorBrush)ColorBrush).Color;
            return new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x38, c.R, c.G, c.B));
        }
    }

    ImageSource? _avatar;
    bool _avatarLoaded;
    [JsonIgnore]
    public ImageSource? Avatar
    {
        get
        {
            if (_avatarLoaded) return _avatar;
            _avatarLoaded = true;
            var path = AccountStore.IconPath(Id);
            if (!File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 96;
                bmp.EndInit();
                bmp.Freeze();
                _avatar = bmp;
            }
            catch { }
            return _avatar;
        }
    }
    public void ReloadAvatar() { _avatarLoaded = false; _avatar = null; Changed(nameof(Avatar)); }

    [JsonIgnore]
    public string LastUsedText
    {
        get
        {
            if (LastUsed is not { } t) return "Never used";
            var ago = DateTime.Now - t;
            return ago.TotalMinutes < 1 ? "Used just now"
                : ago.TotalHours < 1 ? $"Used {(int)ago.TotalMinutes} min ago"
                : ago.TotalDays < 1 ? $"Used {(int)ago.TotalHours} h ago"
                : ago.TotalDays < 2 ? "Used yesterday"
                : $"Used {(int)ago.TotalDays} days ago";
        }
    }
    public void RefreshTimes()
    {
        Changed(nameof(LastUsedText)); Changed(nameof(AgeText)); Changed(nameof(IsStale)); Changed(nameof(AgeTip));
        DecayChanged(); PenaltyChanged();
        foreach (var g in Recent) g.Refresh();
    }

    public static readonly string[] Palette =
        ["#E0B95B", "#5BC0EB", "#9B7BFF", "#F2667A", "#4FD1A5", "#F59F45", "#E879C6", "#8FA3B8"];

    public static string RegionName(string platform) => platform.ToUpperInvariant() switch
    {
        "" => "",
        "EUW1" => "EUW", "EUN1" => "EUNE", "NA1" => "NA", "KR" => "KR", "BR1" => "BR", "LA1" => "LAN", "LA2" => "LAS",
        "OC1" => "OCE", "TR1" => "TR", "RU" => "RU", "JP1" => "JP", "PH2" => "PH", "SG2" => "SG", "TH2" => "TH",
        "TW2" => "TW", "VN2" => "VN", "ME1" => "ME", "PBE1" => "PBE",
        var p => p.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'),
    };

    public static Brush TierBrush(string tier)
    {
        var hex = tier switch
        {
            "IRON" => "#8E8A86", "BRONZE" => "#C08458", "SILVER" => "#A9B7C4", "GOLD" => "#E6B54A",
            "PLATINUM" => "#3FC1AE", "EMERALD" => "#33C474", "DIAMOND" => "#7F95FF", "MASTER" => "#B866F0",
            "GRANDMASTER" => "#EE5A55", "CHALLENGER" => "#F4D35E", _ => "#8A8D99",
        };
        var b = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void ChangedAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>
/// Saved accounts (accounts.json) and their sessions (sessions\{puuid}.dat, encrypted with Windows DPAPI for the
/// current Windows user, so another user or PC can't read them).
/// </summary>
sealed class AccountStore
{
    public static readonly string Dir = Environment.GetEnvironmentVariable("RIFTSWITCH_DATA") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RiftSwitch");
    static string ListFile => Path.Combine(Dir, "accounts.json");
    static string SessionDir => Path.Combine(Dir, "sessions");
    static string IconDir => Path.Combine(Dir, "icons");
    public static string IconPath(string id) => Path.Combine(IconDir, id + ".jpg");
    static string SessionPath(string id) => Path.Combine(SessionDir, id + ".dat");

    static readonly byte[] Entropy = "RiftSwitch.session.v1"u8.ToArray();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static string BackupFile => Path.Combine(Dir, "accounts.json.bak");

    public ObservableCollection<Account> Accounts { get; } = [];

    /// <summary>Accounts rebuilt from saved sessions that were missing from the list (after a crash damaged it).</summary>
    public int Recovered { get; private set; }
    /// <summary>The list was unreadable and the backup copy was used.</summary>
    public bool UsedBackup { get; private set; }

    string? _lastJson;

    public void Load()
    {
        List<Account>? list = null;
        foreach (var file in new[] { ListFile, BackupFile })
        {
            try
            {
                if (!File.Exists(file)) continue;
                list = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(file));
                if (list == null) continue;
                UsedBackup = file == BackupFile;
                break;
            }
            catch { list = null; }
        }
        // Keep an unreadable list for inspection instead of overwriting it on the next save.
        if ((list == null || UsedBackup) && File.Exists(ListFile))
            try { File.Copy(ListFile, ListFile + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }

        foreach (var a in (list ?? []).OrderBy(a => a.Order))
            if (a.Id != "" && File.Exists(SessionPath(a.Id)) && Find(a.Id) == null) Accounts.Add(a);

        Recovered = RecoverOrphans();
        Renumber();
        if (Recovered > 0 || UsedBackup) try { Save(); } catch { }
    }

    /// <summary>
    /// Re-adds accounts whose saved session exists but which aren't in the list. The name, tag and region come from
    /// the session itself; nicknames, notes and rank history can't be recovered this way.
    /// </summary>
    int RecoverOrphans()
    {
        if (!Directory.Exists(SessionDir)) return 0;
        int added = 0;
        foreach (var path in Directory.GetFiles(SessionDir, "*.dat").OrderBy(File.GetLastWriteTimeUtc))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (Find(id) != null) continue;
            try
            {
                var pack = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                if (SessionData.YamlInPack(pack) is not { } yaml || SessionData.Inspect(yaml) is not { HasSession: true } who || who.Puuid != id)
                    continue;
                Accounts.Add(new Account
                {
                    Id = id,
                    GameName = who.GameName ?? "",
                    TagLine = who.TagLine ?? "",
                    Platform = who.Platform ?? "",
                    SavedAt = File.GetLastWriteTime(path),
                    Color = Account.Palette[Accounts.Count % Account.Palette.Length],
                });
                added++;
            }
            catch { /* not ours to read (other Windows user) or damaged: leave it */ }
        }
        return added;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        for (int i = 0; i < Accounts.Count; i++) Accounts[i].Order = i;
        var json = JsonSerializer.Serialize(Accounts.ToList(), Json);
        if (json == _lastJson) return;   // nothing changed: don't rewrite the file every few seconds
        WriteDurably(ListFile, Encoding.UTF8.GetBytes(json), BackupFile);
        _lastJson = json;
    }

    /// <summary>
    /// Writes a file so that a crash or power cut leaves either the old or the new version, never an empty one:
    /// the data is flushed to the disk before it replaces the old file, and the previous version is kept as a backup.
    /// </summary>
    internal static void WriteDurably(string path, byte[] data, string? backup = null)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true);
        }
        if (backup != null && File.Exists(path))
            try { File.Replace(tmp, path, backup, ignoreMetadataErrors: true); return; } catch { }
        File.Move(tmp, path, true);
    }

    public Account? Find(string? id) => id == null ? null : Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>Stores a session pack for an account, adding the account if it's new. Returns it and whether it's new.</summary>
    public (Account Account, bool IsNew) Upsert(SessionIdentity who, byte[] pack)
    {
        var acc = Find(who.Puuid);
        bool isNew = acc == null;
        if (acc == null)
        {
            acc = new Account { Id = who.Puuid!, Color = Account.Palette[Accounts.Count % Account.Palette.Length] };
            Accounts.Add(acc);
        }
        if (!string.IsNullOrEmpty(who.GameName)) acc.GameName = who.GameName;
        if (!string.IsNullOrEmpty(who.TagLine)) acc.TagLine = who.TagLine;
        if (!string.IsNullOrEmpty(who.Platform)) acc.Platform = who.Platform;
        acc.SavedAt = DateTime.Now;
        acc.NeedsSignIn = false;
        acc.ChangedAll();

        Directory.CreateDirectory(SessionDir);
        WriteDurably(SessionPath(acc.Id), ProtectedData.Protect(pack, Entropy, DataProtectionScope.CurrentUser));
        Renumber();
        Save();
        return (acc, isNew);
    }

    public byte[] LoadSession(Account acc) =>
        ProtectedData.Unprotect(File.ReadAllBytes(SessionPath(acc.Id)), Entropy, DataProtectionScope.CurrentUser);

    public void Remove(Account acc)
    {
        Accounts.Remove(acc);
        try { File.Delete(SessionPath(acc.Id)); } catch { }
        try { File.Delete(IconPath(acc.Id)); } catch { }
        Renumber();
        Save();
    }

    /// <summary>Every tag used on any account, sorted.</summary>
    public List<string> AllTags() =>
        Accounts.SelectMany(a => a.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Moves an account to a position in the list (drag to reorder). Saved by the caller when the drag ends.</summary>
    public void MoveTo(Account acc, int index)
    {
        int i = Accounts.IndexOf(acc);
        if (i < 0 || index < 0 || index >= Accounts.Count || i == index) return;
        Accounts.Move(i, index);
        Renumber();
    }

    public void Move(Account acc, int delta)
    {
        int i = Accounts.IndexOf(acc), j = i + delta;
        if (i < 0 || j < 0 || j >= Accounts.Count) return;
        Accounts.Move(i, j);
        Renumber();
        Save();
    }

    public void Renumber()
    {
        for (int i = 0; i < Accounts.Count; i++) Accounts[i].Slot = i + 1;
    }

    public static void SaveIcon(string id, byte[] bytes)
    {
        Directory.CreateDirectory(IconDir);
        File.WriteAllBytes(IconPath(id), bytes);
    }
}
