using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blackbox.Core;

/// <summary>Rules for <c>bbx check</c>. Trace: a regex for requirement IDs. Base: only lines added since the merge base with this ref.</summary>
public sealed record CheckOptions(Regex? Trace, bool RequireTests, string? Base);

/// <summary>
/// One file with AI-written lines. Traced is null when the trace rule doesn't apply (no rule, or not source);
/// when false, TraceIssue says why. HasTests is null when the test rule doesn't apply (not source, or a test file itself).
/// </summary>
public sealed record CheckFile(
    string File, int AiLines, IReadOnlyList<LineOrigin> Origins,
    bool? Traced, IReadOnlyList<string> TraceIds, string? TraceIssue, bool? HasTests);

public sealed record CheckResult(CheckOptions Options, IReadOnlyList<CheckFile> Files, IReadOnlyList<SessionLog> BrokenChains)
{
    public int Untraced => Files.Count(f => f.Traced == false);
    public int Untested => Options.RequireTests ? Files.Count(f => f.HasTests == false) : 0;
    public bool Passed => BrokenChains.Count == 0 && Untraced == 0 && Untested == 0;

    /// <summary>Findings in DO-178C terms, one line each. Empty when everything passes.</summary>
    public IReadOnlyList<string> Summary()
    {
        var lines = new List<string>();
        if (Untraced > 0)
        {
            lines.Add($"Untraced AI code: {Files(Untraced)} (no requirement link)");
        }
        if (Untested > 0)
        {
            lines.Add($"AI source changes without test evidence: {Files(Untested)}");
        }
        if (BrokenChains.Count > 0)
        {
            lines.Add($"Record integrity: {BrokenChains.Count} session log{(BrokenChains.Count == 1 ? "" : "s")} failed hash chain verification");
        }
        return lines;

        static string Files(int n) => n == 1 ? "1 file" : $"{n.ToString(CultureInfo.InvariantCulture)} files";
    }
}

public static partial class Checker
{
    /// <summary>
    /// Rules apply to source files (see <see cref="BlackboxConfig.IsSource"/>); docs are listed but not judged.
    /// AI code is what <see cref="Provenance.Blame"/> attributes to a recorded agent edit. With a base ref, only lines
    /// added since the merge base count. A line traces when its prompt, or an earlier prompt in the same session,
    /// matches the trace regex. A source file has test evidence when every session behind it ran tests or changed a test file.
    /// </summary>
    public static CheckResult Run(string root, IReadOnlyList<SessionLog> sessions, BlackboxConfig config, CheckOptions options)
    {
        var mergeBase = options.Base is null ? null : Investigator.Git(root, "merge-base", options.Base, "HEAD").Trim();
        var candidates = sessions
            .SelectMany(s => s.Records.Where(r => r.IsFileChange()))
            .Select(r => r.O("data").S("file"))
            .OfType<string>()
            .Where(f => !f.StartsWith("<outside>", StringComparison.Ordinal) && !config.IsIgnored(f) && File.Exists(Path.Combine(root, f)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        var files = new List<CheckFile>();
        foreach (var file in candidates)
        {
            var added = mergeBase is null ? null : AddedLines(root, mergeBase, file);
            var origins = Provenance.Blame(root, file, sessions).Lines
                .Where(l => l.Origin is not null && (added is null || added.Contains(l.Number)))
                .Select(l => l.Origin!)
                .ToList();
            if (origins.Count == 0)
            {
                continue;
            }
            var distinct = origins.DistinctBy(o => (o.Session, o.Prompt)).ToList();
            var source = config.IsSource(file);
            var (ids, issue) = source ? Trace(distinct, options.Trace) : ([], null);
            bool? traced = options.Trace is null || !source ? null : ids.Count > 0;
            bool? hasTests = source && !IsTestPath(file)
                ? distinct.Select(o => o.Session).Distinct().All(HasTestEvidence)
                : null;
            files.Add(new(file, origins.Count, distinct, traced, ids, issue, hasTests));
        }

        return new(options, files, [.. sessions.Where(s => !s.Verify.Ok)]);
    }

    private static (IReadOnlyList<string> Ids, string? Issue) Trace(IReadOnlyList<LineOrigin> origins, Regex? trace)
    {
        if (trace is null)
        {
            return ([], null);
        }
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var o in origins)
        {
            if (o.Prompt is null)
            {
                return ([], "no prompt recorded before the edit");
            }
            var prompts = o.Session.Prompts.Where(p => p.Seq() <= o.Prompt.Seq()).ToList();
            if (prompts.All(p => p.O("data").S("prompt") is null))
            {
                return ([], "prompt not recorded (record_prompts is hash or off)");
            }
            var found = prompts.SelectMany(p => trace.Matches(p.O("data").S("prompt") ?? "").Select(m => m.Value)).ToList();
            if (found.Count == 0)
            {
                return ([], "no requirement ID in the prompts behind it");
            }
            ids.UnionWith(found);
        }
        return ([.. ids], null);
    }

    private static bool HasTestEvidence(SessionLog s) =>
        s.Records.Any(r => r.Kind() == "TestCompleted" || (r.IsFileChange() && IsTestPath(r.O("data").S("file") ?? "")));

    /// <summary>Test files by convention: a tests/spec path segment or name part, or a name ending in Test(s)/Spec.</summary>
    public static bool IsTestPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return TestSegment().IsMatch(path)
            || name.EndsWith("Test", StringComparison.Ordinal) || name.EndsWith("Tests", StringComparison.Ordinal)
            || name.EndsWith("Spec", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(^|[/\\._-])(tests?|specs?|__tests__)([/\\._-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TestSegment();

    /// <summary>Line numbers (in the working tree) added since <paramref name="mergeBase"/>. Untracked files count as all new.</summary>
    private static HashSet<int> AddedLines(string root, string mergeBase, string file)
    {
        var set = new HashSet<int>();
        if (Investigator.Git(root, "ls-files", "--", file).Trim().Length == 0)
        {
            if (Investigator.Git(root, "ls-files", "--others", "--exclude-standard", "--", file).Trim().Length == 0)
            {
                return set; // git-ignored: never part of a change
            }
            var count = File.ReadLines(Path.Combine(root, file)).Count();
            set.UnionWith(Enumerable.Range(1, count));
            return set;
        }
        foreach (var line in Investigator.Git(root, "diff", "-U0", "--no-color", "--no-ext-diff", mergeBase, "--", file).Split('\n'))
        {
            var m = Hunk().Match(line);
            if (m.Success)
            {
                var start = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var len = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
                set.UnionWith(Enumerable.Range(start, len));
            }
        }
        return set;
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex Hunk();
}
