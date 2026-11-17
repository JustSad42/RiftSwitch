using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace RiftSwitch;

/// <summary>
/// Plain-text log of switches (%APPDATA%\RiftSwitch\switch.log): each step with its timing, and a short
/// fingerprint of the session that was saved or restored. Fingerprints are the first characters of a SHA-256
/// hash, so the log never contains a token, but a stale or rejected session can still be traced.
/// </summary>
static class SwitchLog
{
    static string FilePath => Path.Combine(AccountStore.Dir, "switch.log");
    static readonly Stopwatch Clock = new();
    static readonly object Gate = new();

    public static void Begin(string what)
    {
        Clock.Restart();
        Write("");
        Write($"==== {what}");
    }

    public static void Step(string what) => Write($"{Clock.Elapsed.TotalSeconds,6:0.00}s  {what}");

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AccountStore.Dir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 512 * 1024)
                    File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, (line == "" ? "" : $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}") + Environment.NewLine);
            }
            catch { /* logging must never break switching */ }
        }
    }

    /// <summary>"acct 6732 · token 194D3BC9 #2": account, refresh-token fingerprint and Riot's write counter.</summary>
    public static string Describe(string? yaml)
    {
        if (yaml == null) return "no session file";
        var who = SessionData.Inspect(yaml);
        if (!who.HasSession) return "signed out";
        var token = System.Text.RegularExpressions.Regex.Match(yaml, @"refresh_token:\s*""?([^""\s]+)");
        var count = System.Text.RegularExpressions.Regex.Match(yaml, @"refresh_token_write_count:\s*(\d+)");
        var print = token.Success ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Groups[1].Value)))[..8] : "?";
        return $"acct {who.Puuid?[..Math.Min(4, who.Puuid.Length)]} · token {print} #{(count.Success ? count.Groups[1].Value : "?")}";
    }
}
