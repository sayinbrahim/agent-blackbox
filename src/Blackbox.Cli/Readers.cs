using System.Globalization;
using System.Text;
using Blackbox.Core;
using Blackbox.Report;

namespace Blackbox.Cli;

/// <summary>ANSI colors, off when NO_COLOR is set or output is redirected.</summary>
public sealed class Ansi(bool enabled)
{
    public static Ansi Auto() => new(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) && !Console.IsOutputRedirected);

    public string Dim(string s) => Wrap("2", s);
    public string Bold(string s) => Wrap("1", s);
    public string Red(string s) => Wrap("31", s);
    public string Green(string s) => Wrap("32", s);
    public string Amber(string s) => Wrap("33", s);

    private string Wrap(string code, string s) => enabled ? $"\u001b[{code}m{s}\u001b[0m" : s;
}

/// <summary>The read-side commands. Each returns an exit code and writes to the given writer.</summary>
public static class Readers
{
    public static (string Root, BlackboxConfig Config, List<SessionLog> Sessions) Open(string dir)
    {
        var root = HookRecorder.FindRoot(dir) ?? throw new InvalidOperationException("No .blackbox/ found. Run 'bbx init' first.");
        var config = BlackboxConfig.Load(Path.Combine(root, HookRecorder.DirName, "config.json"));
        return (root, config, SessionLog.LoadAll(root));
    }

    public static int Sessions(string dir, TextWriter o, Ansi c)
    {
        var (_, config, sessions) = Open(dir);
        if (sessions.Count == 0)
        {
            o.WriteLine("No recorded sessions yet.");
            return 0;
        }
        o.WriteLine(c.Dim($"{"START (UTC)",-20} {"DURATION",-9} {"SESSION",-9} {"PROMPTS",7} {"TOOLS",6} {"FILES",6}  {"CHAIN",-9} VERIFICATION"));
        foreach (var s in sessions)
        {
            var a = new SessionAnalysis(s, config);
            var chain = s.Verify.Ok ? c.Green("ok".PadRight(9)) : c.Red("TAMPERED ");
            o.WriteLine($"{s.Start.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),-20} {Duration(a.Duration),-9} {s.ShortId,-9} {a.PromptCount,7} {a.ToolCalls,6} {a.Files.Count,6}  {chain} {Freshness(a.Freshness, c)}");
        }
        return 0;
    }

    public static int Timeline(string dir, string? session, TextWriter o, Ansi c)
    {
        var (_, config, sessions) = Open(dir);
        var a = new SessionAnalysis(SessionLog.Find(sessions, session), config);
        var s = a.Session;
        o.WriteLine(c.Bold($"Session {s.Id}"));
        o.WriteLine(c.Dim($"{s.Start.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC · {Duration(a.Duration)} · branch {s.Branch ?? "unknown"} · {a.PromptCount} prompts · {a.ToolCalls} tool calls · {a.Files.Count} files · {a.Errors} errors"));
        o.WriteLine($"Chain: {(s.Verify.Ok ? c.Green($"verified ({s.Verify.Records} records)") : c.Red("TAMPERED: " + s.Verify.Error))}");
        o.WriteLine($"Verification: {Freshness(a.Freshness, c)}  {c.Dim(a.Freshness.Message)}");
        if (a.Freshness.State == VerificationState.Stale)
        {
            o.WriteLine(c.Amber("⚠ " + a.Freshness.Message));
        }
        foreach (var g in a.Groups)
        {
            o.WriteLine();
            o.WriteLine(g.Prompt is { } p
                ? c.Amber($"#{g.Number} {SessionAnalysis.Clock(p)} ") + c.Bold(SessionAnalysis.OneLine(SessionAnalysis.Describe(p), 110))
                : c.Amber(g.Resumed ? "(resumed, no prompt yet)" : "(before first prompt)"));
            foreach (var r in g.Actions)
            {
                var failed = r.Event() == "PostToolUseFailure";
                var system = r.Kind() is "SessionStarted" or "SessionEnded" or "AgentStopped" || r.IsNotification();
                var mark = failed ? c.Red("✖") : system ? c.Dim("·") : c.Green("✔");
                var text = SessionAnalysis.Describe(r);
                o.WriteLine($"  {c.Dim(SessionAnalysis.Clock(r))} {mark} {(failed ? c.Red(text) : system ? c.Dim(text) : text)}");
            }
        }
        if (a.Anomalies.Count > 0)
        {
            o.WriteLine();
            o.WriteLine(c.Bold("Anomalies"));
            foreach (var an in a.Anomalies)
            {
                o.WriteLine($"  {c.Amber(an.Kind)} {an.Message}");
            }
        }
        return 0;
    }

    public static int Blame(string dir, string target, TextWriter o, Ansi c)
    {
        var (root, _, sessions) = Open(dir);
        var (file, line) = SplitLine(target);
        var full = Path.GetFullPath(file, dir);
        if (!File.Exists(full))
        {
            throw new InvalidOperationException($"File not found: {file}");
        }
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        var result = Provenance.Blame(root, rel, sessions);

        if (line is { } n)
        {
            var l = result.Lines.FirstOrDefault(x => x.Number == n) ?? throw new InvalidOperationException($"{rel} has {result.Lines.Count} lines.");
            if (l.Trivial)
            {
                o.WriteLine($"Line {n}: blank or punctuation only; not attributed.");
            }
            else if (l.Origin is { } origin)
            {
                var r = origin.Record;
                o.WriteLine(c.Bold($"Line {n}: AI-written"));
                o.WriteLine($"  Session : {origin.Session.Start.UtcDateTime:yyyy-MM-dd HH:mm} ({origin.Session.ShortId})");
                o.WriteLine($"  Prompt  : {(origin.Prompt is { } p ? $"#{origin.Session.PromptNumber(p)} \"{SessionAnalysis.OneLine(SessionAnalysis.Describe(p), 100)}\"" : "(no prompt recorded)")}");
                o.WriteLine($"  Tool    : {r.S("tool")} at {SessionAnalysis.Clock(r)}");
                o.WriteLine("  Status  : unchanged since");
            }
            else
            {
                o.WriteLine(c.Bold($"Line {n}: human-written or changed after the agent wrote it"));
                o.WriteLine("  No recorded agent edit added this exact line.");
            }
            o.WriteLine(c.Dim($"  {l.Text.Trim()}"));
            return 0;
        }

        o.WriteLine(c.Bold($"{rel}: {result.AiPercent}% AI, {100 - result.AiPercent}% human") + c.Dim($"  ({result.AiLines} of {result.Counted} non-trivial lines)"));
        if (result.FileNote is not null)
        {
            o.WriteLine(c.Dim(result.FileNote));
        }
        foreach (var l in result.Lines)
        {
            var tag = l.Trivial ? "        " : l.Origin is { } org ? c.Amber($"AI {(org.Prompt is { } p ? "#" + org.Session.PromptNumber(p) : "?"),-4} ") : c.Dim("human   ");
            o.WriteLine($"{c.Dim(l.Number.ToString(CultureInfo.InvariantCulture).PadLeft(5))} {tag}{l.Text}");
        }
        return 0;
    }

    public static int Report(string dir, string? session, string format, string? output, TextWriter o)
    {
        var (_, config, sessions) = Open(dir);
        var a = new SessionAnalysis(SessionLog.Find(sessions, session), config);
        if (format == "md")
        {
            var md = MarkdownReport.Render(a, Program.Version);
            if (output is null)
            {
                o.Write(md);
            }
            else
            {
                File.WriteAllText(output, md, new UTF8Encoding(false));
                o.WriteLine($"Wrote {output}");
            }
            return 0;
        }
        if (format != "html")
        {
            throw new InvalidOperationException("--format must be html or md");
        }
        output ??= Path.Combine(dir, $"bbx-report-{a.Session.ShortId}.html");
        File.WriteAllText(output, HtmlReport.Render(a, Program.Version), new UTF8Encoding(false));
        o.WriteLine($"Wrote {output}");
        return 0;
    }

    /// <summary>The CI gate. Exit code 1 when a rule fails or a session log fails chain verification.</summary>
    public static int Check(string dir, string? trace, bool requireTests, string? baseRef, TextWriter o, Ansi c)
    {
        var (root, config, sessions) = Open(dir);
        System.Text.RegularExpressions.Regex? regex = null;
        if (trace is not null)
        {
            try
            {
                regex = new(trace, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid --require-trace pattern: {ex.Message}");
            }
        }
        var r = Checker.Run(root, sessions, config, new(regex, requireTests, baseRef));

        var rules = new List<string>();
        if (trace is not null)
        {
            rules.Add($"trace /{trace}/");
        }
        if (requireTests)
        {
            rules.Add("tests");
        }
        var scope = baseRef is null ? "all recorded AI code" : $"AI lines added since {baseRef}";
        o.WriteLine(c.Bold("bbx check") + c.Dim($" · {scope} · {r.Files.Count} file(s), {r.Files.Sum(f => f.AiLines)} line(s) · rules: {(rules.Count == 0 ? "none" : string.Join(", ", rules))}"));

        if (r.Files.Count > 0)
        {
            var width = Math.Max(4, r.Files.Max(f => f.File.Length));
            o.WriteLine();
            o.WriteLine(c.Dim($"  {"FILE".PadRight(width)}  {"AI LINES",8}  {"TRACE",-16} TESTS"));
            foreach (var f in r.Files)
            {
                var traceCell = f.Traced is null ? "-" : f.Traced == true ? string.Join(",", f.TraceIds) : "✖ untraced";
                var testCell = !requireTests || f.HasTests is null ? "-" : f.HasTests == true ? "ok" : "✖ none";
                var traceText = traceCell.StartsWith('✖') ? c.Red(traceCell.PadRight(16)) : traceCell.PadRight(16);
                var testText = testCell.StartsWith('✖') ? c.Red(testCell) : testCell;
                o.WriteLine($"  {f.File.PadRight(width)}  {f.AiLines,8}  {traceText} {testText}");
                if (f.TraceIssue is not null)
                {
                    var where = string.Join("; ", f.Origins.GroupBy(x => x.Session).Select(g =>
                        $"session {g.Key.ShortId} prompt {string.Join(", ", g.Select(x => x.Prompt is { } p ? "#" + g.Key.PromptNumber(p) : "?").Distinct())}"));
                    o.WriteLine(c.Dim($"  {"".PadRight(width)}  {f.TraceIssue} ({where})"));
                }
            }
        }

        o.WriteLine();
        o.WriteLine(r.BrokenChains.Count == 0
            ? c.Dim($"Record integrity: {sessions.Count} session log(s), hash chains verified")
            : c.Red($"Record integrity: {string.Join(", ", r.BrokenChains.Select(s => $"{s.ShortId} ({s.Verify.Error})"))}"));
        foreach (var line in r.Summary())
        {
            o.WriteLine(c.Red("FAIL  ") + line);
        }
        if (r.Passed)
        {
            o.WriteLine(c.Green("PASS  ") + (rules.Count == 0 ? "No rules given; listing only." : "Every file with AI code meets the rules."));
        }
        return r.Passed ? 0 : 1;
    }

    public static int Investigate(string dir, string commit, string? output, TextWriter o)
    {
        var (root, config, sessions) = Open(dir);
        var inv = Investigator.Investigate(root, commit, sessions);
        var md = MarkdownReport.RenderInvestigation(inv, config, Program.Version);
        if (output is null)
        {
            o.Write(md);
        }
        else
        {
            File.WriteAllText(output, md, new UTF8Encoding(false));
            o.WriteLine($"Wrote {output}");
        }
        return 0;
    }

    /// <summary>"src/a.cs:57" → (src/a.cs, 57). A drive letter colon ("C:\x") is not a line suffix.</summary>
    public static (string File, int? Line) SplitLine(string target)
    {
        var i = target.LastIndexOf(':');
        return i > 1 && int.TryParse(target.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (target[..i], n)
            : (target, null);
    }

    private static string Freshness(Freshness f, Ansi c) => f.State switch
    {
        VerificationState.Verified => c.Green(f.Label),
        VerificationState.Failed => c.Red(f.Label),
        _ => c.Amber(f.Label),
    };

    private static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h{d.Minutes:00}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m{d.Seconds:00}s" : $"{d.Seconds}s";
}
