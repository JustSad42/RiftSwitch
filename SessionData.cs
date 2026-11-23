using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RiftSwitch;

/// <summary>Who a stored session belongs to, read from the id_token in RiotGamesPrivateSettings.yaml.</summary>
sealed class SessionIdentity
{
    /// <summary>A "Stay signed in" session (refresh token) is stored.</summary>
    public bool HasSession;
    public string? Puuid;
    public string? GameName, TagLine;
    /// <summary>League platform, e.g. EUW1.</summary>
    public string? Platform;
}

/// <summary>
/// Reads, packs and replaces the Riot Client's Data folder. The whole folder is copied so switching keeps
/// working if Riot spreads the session over more files. Every writer needs the Riot Client to be closed.
/// </summary>
static partial class SessionData
{
    // Trusted-device cookie. It identifies this PC, not an account, and Riot keeps it across its own sign-out.
    const string DeviceSection = "rso-authenticator";
    const string PackMagic = "RSWP1";

    [GeneratedRegex(@"^\s*(id_token|refresh_token)\s*:\s*""?([^""\s]+)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex TokenLine();

    public static SessionIdentity Inspect(string yaml)
    {
        var id = new SessionIdentity();
        foreach (Match m in TokenLine().Matches(yaml))
        {
            var value = m.Groups[2].Value;
            if (value is "null" or "~") continue;
            if (m.Groups[1].Value == "refresh_token") { if (value.Length > 20) id.HasSession = true; continue; }
            ReadIdToken(value, id);
        }
        return id;
    }

    // The background check asks for this every couple of seconds: only read and decode the file when it changed.
    sealed record LiveCache(DateTime Write, long Length, SessionIdentity Who);
    static LiveCache? _liveCache;   // swapped as a whole, so it's safe to read from any thread

    public static SessionIdentity InspectLive()
    {
        try
        {
            var info = new FileInfo(Riot.SessionFile);
            if (!info.Exists) return new SessionIdentity();
            if (_liveCache is { } c && c.Write == info.LastWriteTimeUtc && c.Length == info.Length) return c.Who;
            var who = Inspect(ReadShared(Riot.SessionFile));
            _liveCache = new LiveCache(info.LastWriteTimeUtc, info.Length, who);
            return who;
        }
        catch { return new SessionIdentity(); }
    }

    static void ReadIdToken(string jwt, SessionIdentity id)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var r = doc.RootElement;
            if (r.TryGetProperty("sub", out var sub)) id.Puuid = sub.GetString()?.ToLowerInvariant();
            if (r.TryGetProperty("acct", out var acct) && acct.ValueKind == JsonValueKind.Object)
            {
                if (acct.TryGetProperty("game_name", out var g)) id.GameName = g.GetString();
                if (acct.TryGetProperty("tag_line", out var t)) id.TagLine = t.GetString();
            }
            if (r.TryGetProperty("lol_region", out var regions) && regions.ValueKind == JsonValueKind.Array)
            {
                foreach (var reg in regions.EnumerateArray())
                {
                    if (reg.ValueKind != JsonValueKind.Object || !reg.TryGetProperty("cpid", out var cpid)) continue;
                    bool active = reg.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                    if (active || id.Platform == null) id.Platform = cpid.GetString();
                }
            }
        }
        catch { /* not a readable token */ }
    }

    /// <summary>Packs every file of the Data folder into one buffer.</summary>
    public static byte[] Capture()
    {
        var root = Path.GetFullPath(Riot.DataDir).TrimEnd('\\') + '\\';
        var files = Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer, Encoding.UTF8);
        w.Write(PackMagic);
        w.Write(files.Length);
        foreach (var file in files)
        {
            var content = ReadSharedBytes(file);
            w.Write(file[root.Length..]);
            w.Write(content.Length);
            w.Write(content);
        }
        w.Flush();
        return buffer.ToArray();
    }

    /// <summary>The session settings file inside a pack (to read its identity).</summary>
    public static string? YamlInPack(byte[] pack)
    {
        foreach (var (name, content) in Unpack(pack))
            if (name.Equals("RiotGamesPrivateSettings.yaml", StringComparison.OrdinalIgnoreCase))
                return Encoding.UTF8.GetString(content);
        return null;
    }

    static List<(string Name, byte[] Content)> Unpack(byte[] pack)
    {
        var list = new List<(string, byte[])>();
        using var r = new BinaryReader(new MemoryStream(pack), Encoding.UTF8);
        if (r.ReadString() != PackMagic) throw new InvalidDataException("This saved session has an unknown format.");
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++) list.Add((r.ReadString(), r.ReadBytes(r.ReadInt32())));
        return list;
    }

    /// <summary>Replaces the Data folder with a pack made by <see cref="Capture"/>.</summary>
    public static void Restore(byte[] pack)
    {
        var root = Path.GetFullPath(Riot.DataDir).TrimEnd('\\') + '\\';
        var files = new List<(string Path, byte[] Content)>();
        foreach (var (name, content) in Unpack(pack))
        {
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This saved session points outside the Riot Client folder.");
            files.Add((target, content));
        }

        // Read completely before anything is deleted: a damaged pack must not cost the live session.
        Retry(() =>
        {
            Clear(root);
            foreach (var (path, content) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, content);
            }
        });
    }

    /// <summary>
    /// Leaves the Data folder as Riot's own "Sign out" does: no session, trusted-device cookie kept,
    /// so the next sign-in isn't treated as coming from a new PC.
    /// </summary>
    public static void SignOut()
    {
        var path = Riot.SessionFile;
        var device = File.Exists(path) ? DeviceBlock(ReadShared(path)) : null;
        Retry(() =>
        {
            Clear(Riot.DataDir);
            if (device != null)
            {
                Directory.CreateDirectory(Riot.DataDir);
                File.WriteAllText(path, device, new UTF8Encoding(false));
            }
        });
    }

    static string? DeviceBlock(string yaml)
    {
        var block = new StringBuilder();
        bool inside = false;
        foreach (var line in yaml.Split('\n'))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#' && line[0] != '-')
                inside = line.Split(':')[0].Trim().Trim('"', '\'') == DeviceSection;
            if (inside && line.Trim().Length > 0) block.Append(line.TrimEnd('\r')).Append('\n');
        }
        return block.Length > 0 ? block.ToString() : null;
    }

    static void Clear(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
        foreach (var d in Directory.GetDirectories(dir)) Directory.Delete(d, true);
    }

    // The Riot Client rewrites these files while it runs.
    public static string ReadShared(string path) => Encoding.UTF8.GetString(ReadSharedBytes(path));

    static byte[] ReadSharedBytes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>File handles of a process that just exited can linger for a moment.</summary>
    static void Retry(Action action)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 8) { Thread.Sleep(200); }
        }
    }
}
