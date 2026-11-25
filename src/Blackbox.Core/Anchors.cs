using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

/// <summary>A session's chain head as written into a commit: session id, record seq and record hash.</summary>
public sealed record Anchor(string Session, int Seq, string Hash);

/// <summary>One anchor found in git history. Problem is null when the session log still has that exact record.</summary>
public sealed record AnchorCheck(string Commit, Anchor Anchor, string? Problem);

/// <summary>
/// Anchoring writes chain heads into commit trailers (<c>Blackbox-Head: &lt;session&gt; &lt;seq&gt; sha256:…</c>).
/// Someone who rewrites a session log can recompute its whole chain, but not the hashes already in pushed commits.
/// This raises the bar; it is still not tamper-proof (history itself can be rewritten before anyone else has it).
/// </summary>
public static class Anchors
{
    public const string Key = "Blackbox-Head";
    private const string HookMarker = "# agent-blackbox";

    public static string Format(Anchor a) => $"{a.Session} {a.Seq.ToString(CultureInfo.InvariantCulture)} {a.Hash}";

    public static Anchor? Parse(string value)
    {
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq) && parts[2].StartsWith("sha256:", StringComparison.Ordinal)
            ? new(parts[0], seq, parts[2])
            : null;
    }

    /// <summary>The last record of every session log staged for the next commit (the staged content, not the working tree).</summary>
    public static List<Anchor> StagedHeads(string root)
    {
        var heads = new List<Anchor>();
        var paths = Investigator.Git(root, "diff", "--cached", "--name-only", "--relative", "--diff-filter=AM", "--", ".blackbox/sessions")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f.EndsWith(".jsonl", StringComparison.Ordinal));
        foreach (var path in paths)
        {
            var last = Investigator.Git(root, "show", ":./" + path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (last is not null && JsonNode.Parse(last) is JsonObject r && r.S("session") is { } s && r.I("seq") is { } seq && r.S("hash") is { } hash)
            {
                heads.Add(new(s, seq, hash));
            }
        }
        return heads;
    }

    /// <summary>Adds a trailer per staged session head to a commit message file. Returns how many heads were anchored.</summary>
    public static int AddToMessage(string root, string messageFile)
    {
        var heads = StagedHeads(root);
        if (heads.Count == 0)
        {
            return 0;
        }
        var args = new List<string> { "interpret-trailers", "--in-place", "--if-exists", "addIfDifferent" };
        foreach (var h in heads)
        {
            args.Add("--trailer");
            args.Add($"{Key}: {Format(h)}");
        }
        args.Add(Path.GetFullPath(messageFile));
        Investigator.Git(root, [.. args]);
        return heads.Count;
    }

    /// <summary>Checks every anchor in the history of HEAD against the session logs. Empty outside git or without anchors.</summary>
    public static List<AnchorCheck> Verify(string root, IReadOnlyList<SessionLog> sessions)
    {
        string log;
        try
        {
            log = Investigator.Git(root, "log", $"--format=%H%x1f%(trailers:key={Key},valueonly,separator=%x1e)%x1d", "HEAD");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return []; // not a git repository, no commits, or no git
        }

        var checks = new List<AnchorCheck>();
        foreach (var entry in log.Split('\x1d', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = entry.Split('\x1f');
            if (fields.Length < 2)
            {
                continue;
            }
            foreach (var value in fields[1].Split('\x1e', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Parse(value) is not { } a)
                {
                    continue;
                }
                var session = sessions.FirstOrDefault(s => s.Id == a.Session);
                var record = session?.Records.FirstOrDefault(r => r.Seq() == a.Seq);
                var problem = session is null ? "session log is missing"
                    : record is null ? $"record #{a.Seq} is missing"
                    : record.S("hash") != a.Hash ? $"record #{a.Seq} differs from the one anchored in this commit"
                    : null;
                checks.Add(new(fields[0], a, problem));
            }
        }
        return checks;
    }

    /// <summary>
    /// Installs a prepare-commit-msg hook that anchors staged session heads. Replaces an earlier bbx hook, never someone
    /// else's. The hook can't block a commit. Returns the hook path.
    /// </summary>
    public static string InstallHook(string root, string bbxCommand)
    {
        var rel = Investigator.Git(root, "rev-parse", "--git-path", "hooks/prepare-commit-msg").Trim();
        var path = Path.GetFullPath(Path.Combine(root, rel));
        if (File.Exists(path) && !File.ReadAllText(path).Contains(HookMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{path} already has a prepare-commit-msg hook. Add this line to it instead:\n  \"{bbxCommand}\" anchor --message-file \"$1\" || true");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            $"#!/bin/sh\n{HookMarker}: anchors recorded session heads in the commit message. It never blocks a commit.\n" +
            $"\"{bbxCommand}\" anchor --message-file \"$1\" >/dev/null 2>&1 || true\nexit 0\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return path;
    }
}
