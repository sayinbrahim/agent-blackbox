using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Blackbox.Core;

namespace Blackbox.Report;

internal static class Fmt
{
    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes:00}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds:00}s" : $"{d.Seconds}s";

    public static string When(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static string ChainHead(SessionLog s) => s.Records.Count > 0 ? s.Records[^1].S("hash") ?? "?" : "(empty)";

    public static string Chain(SessionLog s) => s.Verify.Ok ? $"✔ verified ({s.Verify.Records} records)" : $"✖ tampered: {s.Verify.Error}";

    public static bool Failed(JsonObject r) => r.Event() == "PostToolUseFailure";

    public static bool IsSystem(JsonObject r) => r.Kind() is "SessionStarted" or "SessionEnded" or "AgentStopped" || r.IsNotification();

    public const string Limits =
        "Recorded evidence only. Commands run outside the agent, and edits made in other tools, are not visible. " +
        "Passing tests are not proof of correctness. The hash chain is tamper-evident, not tamper-proof.";
}

/// <summary>Single-file HTML incident report: inline CSS, no scripts, no external requests.</summary>
public static class HtmlReport
{
    private static string? _template;

    public static string Render(SessionAnalysis a, string bbxVersion)
    {
        var s = a.Session;
        var headline = !s.Verify.Ok ? "Chain broken: the log was altered"
            : a.Anomalies.Count > 0 || a.Freshness.State is not VerificationState.Verified ? "Findings require review"
            : "No anomalies in recorded evidence";
        return Template()
            .Replace("{{TITLE}}", E($"bbx report · {s.ShortId}"))
            .Replace("{{HEADLINE}}", E(headline))
            .Replace("{{SESSION}}", E(s.Id))
            .Replace("{{WHEN}}", E(Fmt.When(s.Start)))
            .Replace("{{SUMMARY}}", Summary(a))
            .Replace("{{TIMELINE}}", Timeline(a))
            .Replace("{{ANOMALIES}}", Anomalies(a))
            .Replace("{{FILES}}", Files(a))
            .Replace("{{FOOTER}}", $"Recorded by agent-blackbox v{E(bbxVersion)} — chain head <code>{E(Fmt.ChainHead(s))}</code><br>{E(Fmt.Limits)}");
    }

    private static string Summary(SessionAnalysis a)
    {
        var s = a.Session;
        var sb = new StringBuilder("<div class=\"grid\">");
        Tile(sb, "Start", Fmt.When(s.Start), small: true);
        Tile(sb, "Duration", Fmt.Duration(a.Duration));
        Tile(sb, "Branch", s.Branch is null ? "unknown" : s.Branch + (s.Head is { Length: >= 7 } h ? " @ " + h[..7] : ""), small: true);
        Tile(sb, "Prompts", a.PromptCount.ToString(CultureInfo.InvariantCulture));
        Tile(sb, "Tool calls", a.ToolCalls.ToString(CultureInfo.InvariantCulture));
        Tile(sb, "Files changed", a.Files.Count.ToString(CultureInfo.InvariantCulture));
        Tile(sb, "Errors", a.Errors.ToString(CultureInfo.InvariantCulture));
        sb.Append("</div><div class=\"status\">");
        sb.Append($"<span class=\"badge {(s.Verify.Ok ? "ok" : "bad")}\">CHAIN {E(Fmt.Chain(s).ToUpperInvariant())}</span>");
        var cls = a.Freshness.State switch { VerificationState.Verified => "ok", VerificationState.Failed => "bad", _ => "warn" };
        sb.Append($"<span class=\"badge {cls}\">VERIFICATION {a.Freshness.Label}</span></div>");
        sb.Append($"<p class=\"msg\">{(a.Freshness.State == VerificationState.Stale ? "⚠ " : "")}{E(a.Freshness.Message)}</p>");
        return sb.ToString();
    }

    private static void Tile(StringBuilder sb, string k, string v, bool small = false) =>
        sb.Append($"<div class=\"tile\"><div class=\"k\">{E(k)}</div><div class=\"v{(small ? " small" : "")}\">{E(v)}</div></div>");

    private static string Timeline(SessionAnalysis a)
    {
        if (a.Groups.Count == 0)
        {
            return "<p class=\"empty\">No records.</p>";
        }
        var sb = new StringBuilder("<div class=\"timeline\">");
        foreach (var g in a.Groups)
        {
            var error = g.Actions.Any(Fmt.Failed);
            sb.Append($"<details class=\"prompt{(error ? " has-error" : "")}\" open><summary>");
            if (g.Prompt is { } p)
            {
                var text = SessionAnalysis.Describe(p);
                if (text.Length > 2000)
                {
                    text = text[..2000] + "…";
                }
                sb.Append($"<div class=\"ph\">PROMPT #{g.Number}<span>{SessionAnalysis.Clock(p)} · {g.Actions.Count(r => r.IsToolResult())} tool calls</span></div><div class=\"pt\">{E(text)}</div>");
            }
            else
            {
                sb.Append(g.Resumed
                    ? "<div class=\"ph\">SESSION RESUMED<span>same session, continued later</span></div>"
                    : "<div class=\"ph\">BEFORE FIRST PROMPT<span>actions recorded without a prompt</span></div>");
            }
            sb.Append("</summary>");
            if (g.Actions.Count > 0)
            {
                sb.Append("<ol class=\"actions\">");
                foreach (var r in g.Actions)
                {
                    var failed = Fmt.Failed(r);
                    var (dot, mark) = failed ? ("bad", "✖") : Fmt.IsSystem(r) ? ("sys", "·") : ("ok", "✔");
                    sb.Append($"<li class=\"{(failed ? "fail" : "")}\"><span class=\"t\">{SessionAnalysis.Clock(r)}</span><span class=\"dot {dot}\">{mark}</span><span class=\"d\">{E(SessionAnalysis.Describe(r))}</span></li>");
                }
                sb.Append("</ol>");
            }
            sb.Append("</details>");
        }
        return sb.Append("</div>").ToString();
    }

    private static string Anomalies(SessionAnalysis a)
    {
        if (a.Anomalies.Count == 0)
        {
            return "<p class=\"empty\">None detected.</p>";
        }
        var sb = new StringBuilder("<ul class=\"anoms\">");
        foreach (var an in a.Anomalies)
        {
            sb.Append($"<li class=\"{an.Kind}\"><span class=\"k\">{E(an.Kind)}</span>{E(an.Message)}</li>");
        }
        return sb.Append("</ul>").ToString();
    }

    private static string Files(SessionAnalysis a)
    {
        if (a.Files.Count == 0)
        {
            return "<p class=\"empty\">No files changed.</p>";
        }
        var sb = new StringBuilder("<div class=\"tablewrap\"><table><thead><tr><th>File</th><th>Edits</th><th>Added</th><th>Removed</th><th>Prompts</th></tr></thead><tbody>");
        foreach (var f in a.Files)
        {
            sb.Append($"<tr><td>{E(f.File)}{(f.Created ? " <span class=\"t\">(new)</span>" : "")}</td><td class=\"num\">{f.Edits}</td><td class=\"num plus\">+{f.Added}</td><td class=\"num minus\">−{f.Removed}</td><td>{E(string.Join(", ", f.PromptNumbers.Select(p => "#" + p)))}</td></tr>");
        }
        return sb.Append("</tbody></table></div>").ToString();
    }

    private static string Template()
    {
        if (_template is null)
        {
            using var stream = typeof(HtmlReport).Assembly.GetManifestResourceStream("Blackbox.Report.report.html")
                ?? throw new InvalidOperationException("report template missing");
            using var reader = new StreamReader(stream);
            _template = reader.ReadToEnd();
        }
        return _template;
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
}

/// <summary>Markdown version, for pasting into a pull request.</summary>
public static class MarkdownReport
{
    public static string Render(SessionAnalysis a, string bbxVersion)
    {
        var s = a.Session;
        var sb = new StringBuilder();
        sb.AppendLine($"## Flight report: session `{s.ShortId}`").AppendLine();
        sb.AppendLine("| | |").AppendLine("|---|---|");
        sb.AppendLine($"| Session | `{s.Id}` |");
        sb.AppendLine($"| Start | {Fmt.When(s.Start)} ({Fmt.Duration(a.Duration)}) |");
        sb.AppendLine($"| Branch | {Cell(s.Branch ?? "unknown")}{(s.Head is { Length: >= 7 } h ? $" @ `{h[..7]}`" : "")} |");
        sb.AppendLine($"| Prompts · tool calls · files · errors | {a.PromptCount} · {a.ToolCalls} · {a.Files.Count} · {a.Errors} |");
        sb.AppendLine($"| Chain | {Cell(Fmt.Chain(s))} |");
        sb.AppendLine($"| Verification | **{a.Freshness.Label}**: {Cell(a.Freshness.Message)} |").AppendLine();

        sb.AppendLine("### Timeline").AppendLine();
        foreach (var g in a.Groups)
        {
            sb.AppendLine(g.Prompt is { } p
                ? $"**#{g.Number} · {SessionAnalysis.Clock(p)}** {SessionAnalysis.OneLine(SessionAnalysis.Describe(p), 200)}"
                : g.Resumed ? "**Session resumed**" : "**Before first prompt**").AppendLine();
            foreach (var r in g.Actions.Where(r => !Fmt.IsSystem(r)))
            {
                sb.AppendLine($"- `{SessionAnalysis.Clock(r)}` {(Fmt.Failed(r) ? "✖" : "✔")} {SessionAnalysis.Describe(r)}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("### Anomalies").AppendLine();
        if (a.Anomalies.Count == 0)
        {
            sb.AppendLine("None detected.");
        }
        foreach (var an in a.Anomalies)
        {
            sb.AppendLine($"- **{an.Kind}**: {an.Message}");
        }
        sb.AppendLine().AppendLine("### Files touched").AppendLine();
        if (a.Files.Count == 0)
        {
            sb.AppendLine("No files changed.");
        }
        else
        {
            sb.AppendLine("| File | Edits | + | − | Prompts |").AppendLine("|---|--:|--:|--:|---|");
            foreach (var f in a.Files)
            {
                sb.AppendLine($"| `{f.File}`{(f.Created ? " (new)" : "")} | {f.Edits} | {f.Added} | {f.Removed} | {string.Join(", ", f.PromptNumbers.Select(p => "#" + p))} |");
            }
        }
        sb.AppendLine().AppendLine($"<sub>Recorded by agent-blackbox v{bbxVersion} — chain head `{Fmt.ChainHead(s)}`. {Fmt.Limits}</sub>");
        return sb.ToString();
    }

    /// <param name="config">Used to compute verification freshness as it was at commit time.</param>
    public static string RenderInvestigation(Investigation inv, BlackboxConfig config, string bbxVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Investigation: commit `{inv.Commit[..Math.Min(10, inv.Commit.Length)]}`").AppendLine();
        sb.AppendLine($"> {inv.Subject}").AppendLine();
        sb.AppendLine($"Committed {Fmt.When(inv.Time)}. Window searched: {(inv.ParentTime is { } pt ? Fmt.When(pt) : "start of history")} to commit time.").AppendLine();

        sb.AppendLine("### Changed files").AppendLine();
        sb.AppendLine("| File | Match | Sessions |").AppendLine("|---|---|---|");
        foreach (var f in inv.Files)
        {
            sb.AppendLine($"| `{f.File}` | {f.Confidence} | {string.Join(", ", f.Records.Select(r => "`" + r.Session.ShortId + "`").Distinct())} |");
        }
        var unmatched = inv.Files.Where(f => f.Confidence == "none").ToList();
        if (unmatched.Count > 0)
        {
            sb.AppendLine().AppendLine($"{unmatched.Count} file(s) matched no recorded session. They were changed by a human, by a tool outside the agent, or while recording was off.");
        }
        sb.AppendLine().AppendLine("`exact`: a recorded edit left the file with the committed content. `likely`: the agent edited the file in the window, but the content differs (later edits, or line endings). `none`: no recorded edit.").AppendLine();

        if (inv.Sessions.Count == 0)
        {
            sb.AppendLine("No recorded session touched these files in the window.");
        }
        foreach (var s in inv.Sessions)
        {
            var upToCommit = s.Records.Where(r => r.Ts() <= inv.Time.AddSeconds(1)).ToList();
            var freshness = SessionAnalysis.ComputeFreshness(upToCommit, config);
            sb.AppendLine($"### Session `{s.ShortId}` (started {Fmt.When(s.Start)})").AppendLine();
            sb.AppendLine($"- Chain: {Fmt.Chain(s)}");
            sb.AppendLine($"- Verification at commit time: **{freshness.Label}**: {freshness.Message}");
            var files = inv.Files.Where(f => f.Records.Any(r => r.Session == s)).Select(f => f.File).ToHashSet();
            var prompts = s.Records.Where(r => r.IsFileChange() && files.Contains(r.O("data").S("file") ?? "")).Select(s.PromptOf).OfType<JsonObject>().Distinct();
            sb.AppendLine("- Prompts behind the changes:");
            foreach (var p in prompts)
            {
                sb.AppendLine($"  - #{s.PromptNumber(p)}: {SessionAnalysis.OneLine(SessionAnalysis.Describe(p), 200)}");
            }
            var evidence = upToCommit.Where(r => r.Kind() is "TestCompleted" or "BuildCompleted").ToList();
            sb.AppendLine(evidence.Count == 0 ? "- Build/test evidence before the commit: none recorded" : "- Build/test evidence before the commit:");
            foreach (var r in evidence)
            {
                sb.AppendLine($"  - `{SessionAnalysis.Clock(r)}` {(Fmt.Failed(r) ? "✖" : "✔")} {SessionAnalysis.Describe(r)}");
            }
            sb.AppendLine();
        }
        sb.AppendLine($"<sub>agent-blackbox v{bbxVersion}. {Fmt.Limits}</sub>");
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|");
}
