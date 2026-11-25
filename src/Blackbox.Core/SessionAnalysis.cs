using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blackbox.Core;

public enum VerificationState { Unverified, Verified, Stale, Failed }

public sealed record Freshness(VerificationState State, JsonObject? LastTest, JsonObject? ChangeAfterTest, string Message)
{
    public string Label => State.ToString().ToUpperInvariant();
}

public sealed record TestCounts(int Passed, int Failed, int Skipped, int Total)
{
    public override string ToString() => $"{Passed} passed, {Failed} failed, {Skipped} skipped";
}

public sealed record Anomaly(string Kind, string Message, int? Seq);

public sealed record FileTouch(string File, int Edits, int Added, int Removed, bool Created, IReadOnlyList<int> PromptNumbers);

/// <summary>
/// A prompt and everything the agent did for it. Prompt is null for actions before the first prompt,
/// or (Resumed) for a resume, compact or clear inside the session. Number is the 1-based prompt number, 0 without a prompt.
/// </summary>
public sealed record PromptGroup(JsonObject? Prompt, int Number, bool Resumed, IReadOnlyList<JsonObject> Actions);

/// <summary>Deterministic facts derived from one session. No guessing: what isn't recorded is reported as unknown.</summary>
public sealed partial class SessionAnalysis
{
    public SessionLog Session { get; }
    public Freshness Freshness { get; }
    public IReadOnlyList<Anomaly> Anomalies { get; }
    public IReadOnlyList<FileTouch> Files { get; }
    public IReadOnlyList<PromptGroup> Groups { get; }
    public int PromptCount { get; }
    public int ToolCalls { get; }
    public int Errors { get; }
    public TimeSpan Duration => Session.End - Session.Start;

    public SessionAnalysis(SessionLog session, BlackboxConfig config)
    {
        Session = session;
        var records = session.Records;
        PromptCount = session.Prompts.Count();
        ToolCalls = records.Count(r => r.IsToolResult());
        Errors = records.Count(r => r.Event() == "PostToolUseFailure");
        Freshness = ComputeFreshness(records, config);
        Files = ComputeFiles(session);
        Anomalies = ComputeAnomalies(records, Files, config);
        Groups = ComputeGroups(session);
    }

    public static Freshness ComputeFreshness(IEnumerable<JsonObject> records, BlackboxConfig config)
    {
        JsonObject? lastTest = null;
        JsonObject? changeAfterTest = null;
        JsonObject? anyChange = null;
        foreach (var r in records)
        {
            if (r.Kind() == "TestCompleted")
            {
                lastTest = r;
                changeAfterTest = null;
            }
            else if (IsSourceChange(r, config))
            {
                anyChange = r;
                changeAfterTest ??= lastTest is null ? null : r;
            }
        }

        if (lastTest is null)
        {
            return new(VerificationState.Unverified, null, null, anyChange is null
                ? "No source changes and no test runs recorded. Verification is UNVERIFIED."
                : "Source changed but no test run was recorded. Verification is UNVERIFIED.");
        }
        if (lastTest.O("data")?["success"] is null)
        {
            return new(VerificationState.Unverified, lastTest, changeAfterTest,
                $"A test command ran at {Clock(lastTest)}, but its result was not recorded (this agent's hooks don't report exit codes). Verification is UNVERIFIED.");
        }
        var passed = lastTest.O("data").B("success") == true;
        if (!passed)
        {
            return new(VerificationState.Failed, lastTest, changeAfterTest, $"Last test run at {Clock(lastTest)} failed. Verification is FAILED.");
        }
        if (changeAfterTest is not null)
        {
            return new(VerificationState.Stale, lastTest, changeAfterTest,
                $"Source changed at {Clock(changeAfterTest)} after last passing test at {Clock(lastTest)}. Verification is STALE.");
        }
        return new(VerificationState.Verified, lastTest, null,
            $"Tests passed at {Clock(lastTest)}, after the last source change. This shows the tests passed, not that the code is correct.");
    }

    private static bool IsSourceChange(JsonObject r, BlackboxConfig config) =>
        r.Kind() is "FileCreated" or "FileModified" or "FileDeleted"
        && r.O("data").B("success") != false
        && config.IsSource(r.O("data").S("file"));

    private static List<FileTouch> ComputeFiles(SessionLog session) =>
        [.. session.Records.Where(r => r.IsFileChange() && r.O("data").B("success") != false)
            .GroupBy(r => r.O("data").S("file") ?? "?")
            .Select(g => new FileTouch(
                g.Key,
                g.Count(),
                g.Sum(r => r.O("data").I("added_lines") ?? 0),
                g.Sum(r => r.O("data").I("removed_lines") ?? 0),
                g.Any(r => r.Kind() == "FileCreated"),
                [.. g.Select(session.PromptOf).OfType<JsonObject>().Select(session.PromptNumber).Distinct().Order()]))
            .OrderByDescending(f => f.Edits).ThenBy(f => f.File, StringComparer.Ordinal)];

    private static List<Anomaly> ComputeAnomalies(IReadOnlyList<JsonObject> records, IReadOnlyList<FileTouch> files, BlackboxConfig config)
    {
        var list = new List<Anomaly>();
        foreach (var r in records.Where(r => r.Event() == "PostToolUseFailure"))
        {
            list.Add(new("failed-tool-call", $"{Describe(r)} failed at {Clock(r)}", r.Seq()));
        }
        foreach (var f in files.Where(f => f.Edits >= 3))
        {
            list.Add(new("oscillation", $"{f.File} was edited {f.Edits} times", null));
        }
        foreach (var r in records.Where(r => r.Kind() == "ToolBlocked"))
        {
            list.Add(new("blocked", $"{Describe(r)} at {Clock(r)}", r.Seq()));
        }
        foreach (var r in records.Where(r => r.Kind() == "FileDeleted"))
        {
            list.Add(new("deleted-files", $"Files deleted: {Describe(r)} at {Clock(r)}", r.Seq()));
        }
        if (records.Any(r => IsSourceChange(r, config)) && !records.Any(r => r.Kind() == "TestCompleted"))
        {
            list.Add(new("no-tests", "Source files changed but no test command was run in this session", null));
        }
        return list;
    }

    private static List<PromptGroup> ComputeGroups(SessionLog session)
    {
        var groups = new List<PromptGroup>();
        JsonObject? prompt = null;
        var resumed = false;
        var actions = new List<JsonObject>();
        void Flush()
        {
            if (prompt is not null || actions.Count > 0)
            {
                groups.Add(new(prompt, prompt is null ? 0 : session.PromptNumber(prompt), resumed, actions));
            }
        }
        foreach (var r in session.Records)
        {
            if (r.IsPrompt() || r.IsRestart())
            {
                Flush();
                prompt = r.IsPrompt() ? r : null;
                resumed = r.IsRestart();
                actions = prompt is null ? [r] : [];
            }
            else if (r.Event() != "PreToolUse")
            {
                actions.Add(r);
            }
        }
        Flush();
        return groups;
    }

    /// <summary>One-line description of a record, e.g. "Edit src/a.cs (+12 -3)" or "Bash `dotnet test` (exit 1)".</summary>
    public static string Describe(JsonObject r)
    {
        var d = r.O("data");
        var input = d.O("input");
        var tool = r.S("tool");
        switch (r.Kind())
        {
            case "SessionStarted" when d.S("source") is "resume" or "compact" or "clear":
                return $"Session {(d.S("source") == "resume" ? "resumed" : d.S("source") == "compact" ? "compacted" : "cleared")} on {d.S("git_branch") ?? "unknown branch"}";
            case "SessionStarted":
                return $"Session started ({d.S("source") ?? "?"}{(d.S("agent") is { } ag ? ", " + ag : "")}) on {d.S("git_branch") ?? "unknown branch"}";
            case "TaskNotification":
                return $"Background task notification: {d.S("summary") ?? "task finished"}";
            case "PromptSubmitted" when r.IsNotification():
                return $"Background task notification: {RecordBuilder.NotificationSummary(d.S("prompt")!)}";
            case "SessionEnded":
                return $"Session ended ({d.S("reason") ?? "?"})";
            case "AgentStopped":
                return "Agent finished responding";
            case "PromptSubmitted":
                return d.S("prompt") ?? (d.S("prompt_hash") is not null ? "(prompt stored as hash)" : "(prompt not recorded)");
            case "ToolBlocked":
                return $"Blocked by guard ({d.S("rule") ?? "?"}): {DescribeTool(r)}";
        }
        return DescribeTool(r);
    }

    private static string DescribeTool(JsonObject r)
    {
        var d = r.O("data");
        var input = d.O("input");
        var tool = r.S("tool");
        if (tool is "Edit" or "Write" or "NotebookEdit")
        {
            var file = d.S("file") ?? input.S("file") ?? "?";
            if (d.B("ignored") == true)
            {
                return $"{tool} {file} (ignored path, no diff)";
            }
            return d.I("added_lines") is { } a ? $"{tool} {file} (+{a} -{d.I("removed_lines") ?? 0})" : $"{tool} {file}";
        }
        if (tool is "Bash" or "PowerShell")
        {
            var cmd = OneLine(input.S("command") ?? "", 80);
            var exit = d.I("exit_code") is { } e ? $" (exit {e})" : "";
            var counts = r.Kind() == "TestCompleted" && ParseTestCounts(d.S("output_tail") ?? d.S("error")) is { } c ? $" · {c}" : "";
            return $"{tool} `{cmd}`{exit}{counts}";
        }
        var target = input.S("file") ?? input.S("path") ?? input.S("pattern") ?? input.S("url") ?? input.S("query") ?? input.S("description");
        return target is null ? tool ?? r.Event() ?? "?" : $"{tool} {OneLine(target, 80)}";
    }

    /// <summary>Parses dotnet test summaries. Returns null when the output has none: counts are never guessed.</summary>
    public static TestCounts? ParseTestCounts(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }
        int p = 0, f = 0, s = 0, t = 0;
        var found = false;
        foreach (Match m in VsTestSummary().Matches(output))
        {
            found = true;
            f += Int(m, "f");
            p += Int(m, "p");
            s += Int(m, "s");
            t += Int(m, "t");
        }
        if (!found && TerminalLoggerSummary().Match(output) is { Success: true } m2)
        {
            found = true;
            (t, f, p, s) = (Int(m2, "t"), Int(m2, "f"), Int(m2, "p"), Int(m2, "s"));
        }
        return found ? new TestCounts(p, f, s, t) : null;
    }

    private static int Int(Match m, string g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);

    public static string Clock(JsonObject r) => r.Ts().UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string OneLine(string s, int max)
    {
        var line = string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    // "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12"
    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(?<f>\d+),\s+Passed:\s+(?<p>\d+),\s+Skipped:\s+(?<s>\d+),\s+Total:\s+(?<t>\d+)")]
    private static partial Regex VsTestSummary();

    // "Test summary: total: 12, failed: 0, succeeded: 12, skipped: 0"
    [GeneratedRegex(@"Test summary:\s+total:\s+(?<t>\d+),\s+failed:\s+(?<f>\d+),\s+succeeded:\s+(?<p>\d+),\s+skipped:\s+(?<s>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TerminalLoggerSummary();
}
