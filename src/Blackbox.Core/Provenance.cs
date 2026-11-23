using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

/// <summary>Where a line came from: the record that added it and the prompt behind that record.</summary>
public sealed record LineOrigin(SessionLog Session, JsonObject Record, JsonObject? Prompt);

public sealed record BlameLine(int Number, string Text, bool Trivial, LineOrigin? Origin);

public sealed record BlameResult(string File, IReadOnlyList<BlameLine> Lines, int AiLines, int HumanLines, string? FileNote)
{
    public int Counted => AiLines + HumanLines;
    public int AiPercent => Counted == 0 ? 0 : (int)Math.Round(100.0 * AiLines / Counted);
}

public static class Provenance
{
    /// <summary>
    /// A line counts as AI-written when its normalized hash was added by a recorded agent edit.
    /// Lines that were changed afterwards don't match and count as human (or unrecorded). Trivial lines are not counted.
    /// </summary>
    public static BlameResult Blame(string root, string relFile, IReadOnlyList<SessionLog> sessions)
    {
        var edits = sessions
            .SelectMany(s => s.Records.Where(r => r.IsFileChange()).Select(r => (Session: s, Record: r)))
            .OrderBy(x => x.Record.Ts())
            .ToList();

        // hash -> latest edit of this file that added it. Only the same file counts: matching across files turned
        // boilerplate (usings, namespaces) into false "AI" hits. Lines moved within the file are still found.
        var added = new Dictionary<string, (SessionLog, JsonObject)>();
        foreach (var (s, r) in edits.Where(e => e.Record.O("data").S("file") == relFile))
        {
            foreach (var h in r.O("data")?["added_line_hashes"] as JsonArray ?? [])
            {
                if (h?.GetValue<string>() is { } hash)
                {
                    added[hash] = (s, r);
                }
            }
        }

        var path = Path.Combine(root, relFile);
        var lines = new List<BlameLine>();
        int ai = 0, human = 0, n = 0;
        foreach (var text in File.ReadLines(path))
        {
            n++;
            var hash = LineHasher.Hash(text);
            if (hash is null)
            {
                lines.Add(new(n, text, true, null));
                continue;
            }
            if (added.TryGetValue(hash, out var hit))
            {
                ai++;
                lines.Add(new(n, text, false, new(hit.Item1, hit.Item2, hit.Item1.PromptOf(hit.Item2))));
            }
            else
            {
                human++;
                lines.Add(new(n, text, false, null));
            }
        }

        string? note = null;
        var last = edits.LastOrDefault(e => e.Record.O("data").S("file") == relFile);
        if (last.Record is not null && last.Record.O("data").S("after_hash") is { } after)
        {
            var now = "sha256:" + HashChain.Sha256Hex(File.ReadAllBytes(path));
            note = now == after
                ? $"File unchanged since the last recorded agent edit ({last.Record.S("ts")})."
                : $"File changed outside recorded sessions after the last agent edit ({last.Record.S("ts")}).";
        }
        else if (last.Record is null)
        {
            note = "No recorded agent edits for this file, so its lines count as human or unrecorded.";
        }
        return new(relFile, lines, ai, human, note);
    }
}

public sealed record FileMatch(string File, string Confidence, IReadOnlyList<(SessionLog Session, JsonObject Record)> Records);

public sealed record Investigation(
    string Commit, string Subject, DateTimeOffset Time, DateTimeOffset? ParentTime,
    IReadOnlyList<SessionLog> Sessions, IReadOnlyList<FileMatch> Files);

public static class Investigator
{
    /// <summary>
    /// Sessions with activity between the commit's parent and the commit are candidates. Each changed file is matched:
    /// exact = a recorded after_hash equals the committed content, likely = the file was edited in the window, none = no record.
    /// </summary>
    public static Investigation Investigate(string root, string commitish, IReadOnlyList<SessionLog> sessions)
    {
        var sha = Git(root, "rev-parse", "--verify", commitish + "^{commit}").Trim();
        var meta = Git(root, "show", "-s", "--format=%ct%n%s", sha).Split('\n');
        var time = DateTimeOffset.FromUnixTimeSeconds(long.Parse(meta[0].Trim(), System.Globalization.CultureInfo.InvariantCulture));
        var subject = meta.Length > 1 ? meta[1].Trim() : "";
        DateTimeOffset? parentTime = null;
        try
        {
            parentTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(Git(root, "show", "-s", "--format=%ct", sha + "^").Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (InvalidOperationException)
        {
            // root commit: no lower bound
        }

        var changed = Git(root, "diff-tree", "--no-commit-id", "--name-only", "-r", "--root", sha)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        bool InWindow(JsonObject r) => r.Ts() <= time.AddSeconds(1) && (parentTime is null || r.Ts() >= parentTime);
        var edits = sessions
            .SelectMany(s => s.Records.Where(r => r.IsFileChange() && InWindow(r)).Select(r => (Session: s, Record: r)))
            .ToList();

        var files = new List<FileMatch>();
        foreach (var file in changed)
        {
            var related = edits.Where(e => e.Record.O("data").S("file") == file).ToList();
            var hashes = BlobHashes(root, sha, file);
            var exact = related.Where(e => e.Record.O("data").S("after_hash") is { } h && hashes.Contains(h)).ToList();
            files.Add(exact.Count > 0
                ? new(file, "exact", exact)
                : new(file, related.Count > 0 ? "likely" : "none", related));
        }

        var involved = files.SelectMany(f => f.Records.Select(r => r.Session)).Distinct().OrderBy(s => s.Start).ToList();
        return new(sha, subject, time, parentTime, involved, files);
    }

    /// <summary>Hashes of the committed blob, as stored (LF) and with CRLF, since agent edits hash the working-tree bytes.</summary>
    private static HashSet<string> BlobHashes(string root, string sha, string file)
    {
        var set = new HashSet<string>();
        byte[] blob;
        try
        {
            blob = GitBytes(root, "show", $"{sha}:{file}");
        }
        catch (InvalidOperationException)
        {
            return set; // deleted in this commit
        }
        set.Add("sha256:" + HashChain.Sha256Hex(blob));
        var text = Encoding.UTF8.GetString(blob);
        set.Add("sha256:" + HashChain.Sha256Hex(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"))));
        set.Add("sha256:" + HashChain.Sha256Hex(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));
        return set;
    }

    private static string Git(string root, params string[] args) => Encoding.UTF8.GetString(GitBytes(root, args));

    private static byte[] GitBytes(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        using var ms = new MemoryStream();
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result.Trim()}");
        }
        return ms.ToArray();
    }
}
