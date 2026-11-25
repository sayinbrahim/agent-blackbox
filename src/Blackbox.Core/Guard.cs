using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blackbox.Core;

/// <summary>One pre-flight rule. It matches a shell command (regex), a file edit (globs) or a deleted path (globs).</summary>
public sealed record GuardRule(string Id, string Reason, Regex? Command = null, IReadOnlyList<Regex>? Edit = null, IReadOnlyList<Regex>? Delete = null);

/// <param name="Target">What matched: the command, or the path.</param>
public sealed record GuardBlock(GuardRule Rule, string Tool, string Target);

/// <param name="Input">The (Claude Code-shaped) payload that was blocked.</param>
public sealed record GuardOutcome(string Root, GuardBlock Block, JsonObject Input);

/// <summary>
/// The opt-in pre-flight checklist: stops a tool call before it runs. A seatbelt against accidents, not a security boundary:
/// an agent that wants to can always find another way to run the same thing.
/// </summary>
public static partial class Guard
{
    private static readonly string[] ShellTools = ["Bash", "PowerShell"];
    private static readonly string[] EditTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];
    private static readonly string[] DeleteVerbs = ["rm", "rmdir", "rd", "del", "erase", "unlink", "remove-item", "ri"];

    public static IReadOnlyList<GuardRule> DefaultRules { get; } =
    [
        new("force-push", "Force-pushing rewrites history other people may already have.", Command: ForcePush()),
        new("drop-database", "Dropping a database destroys its data.", Command: DropDatabase()),
        new("delete-migrations", "Deleting a migration breaks every database that already applied it. Add a new migration instead.",
            Delete: [Glob.ToRegex("**/Migrations/**")]),
        new("protect-recordings", "The agent must not change its own flight recording.",
            Edit: [Glob.ToRegex(".blackbox/sessions/**")], Delete: [Glob.ToRegex(".blackbox/sessions/**")]),
    ];

    /// <summary>Default rules minus "disable", plus the config's own rules. Throws InvalidDataException on a bad rule.</summary>
    public static IReadOnlyList<GuardRule> Rules(BlackboxConfig config)
    {
        var guard = config.Guard switch
        {
            null => null,
            JsonObject o => o,
            _ => throw new InvalidDataException("config.json: guard must be an object"),
        };
        var disabled = Strings(guard?["disable"], "disable") ?? [];
        var rules = DefaultRules.Where(r => !disabled.Contains(r.Id)).ToList();
        foreach (var node in guard?["rules"] as JsonArray ?? [])
        {
            rules.Add(Parse(node as JsonObject ?? throw new InvalidDataException("config.json: guard.rules must hold objects")));
        }
        return rules;
    }

    private static GuardRule Parse(JsonObject o)
    {
        var id = o["id"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s : throw new InvalidDataException("config.json: every guard rule needs an \"id\"");
        Regex? command = null;
        if (o["command"] is JsonValue c && c.TryGetValue<string>(out var pattern))
        {
            try
            {
                command = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException($"config.json: guard rule '{id}': bad command regex: {ex.Message}");
            }
        }
        var edit = Strings(o["edit"], id)?.Select(Glob.ToRegex).ToList();
        var delete = Strings(o["delete"], id)?.Select(Glob.ToRegex).ToList();
        if (command is null && edit is null && delete is null)
        {
            throw new InvalidDataException($"config.json: guard rule '{id}' needs \"command\", \"edit\" or \"delete\"");
        }
        var reason = o["reason"] is JsonValue r && r.TryGetValue<string>(out var text) ? text : $"Blocked by guard rule '{id}'.";
        return new GuardRule(id, reason, command, edit, delete);
    }

    private static string[]? Strings(JsonNode? n, string where) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        JsonArray a => [.. a.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var s) ? s : throw new InvalidDataException($"config.json: guard '{where}': expected strings"))],
        _ => throw new InvalidDataException($"config.json: guard '{where}': expected a string or a list of strings"),
    };

    /// <summary>The first rule this PreToolUse payload breaks, or null.</summary>
    public static GuardBlock? Evaluate(JsonObject input, string root, IReadOnlyList<GuardRule> rules)
    {
        var tool = Str(input, "tool_name") ?? "";
        var toolInput = input["tool_input"] as JsonObject;
        root = Path.GetFullPath(root);
        var cwd = Str(input, "cwd") is { Length: > 0 } c ? Path.GetFullPath(c) : root;

        if (ShellTools.Contains(tool) && Str(toolInput, "command") is { } command)
        {
            var bare = Quoted().Replace(command, "''"); // text inside quotes (commit messages, echo) is not a command
            foreach (var rule in rules.Where(r => r.Command?.IsMatch(bare) == true))
            {
                return new(rule, tool, SessionAnalysis.OneLine(command, 120));
            }
            var targets = DeleteTargets(command).Select(t => (Arg: t, Rel: Relative(root, cwd, t))).ToList();
            foreach (var rule in rules.Where(r => r.Delete is not null))
            {
                foreach (var t in targets.Where(t => Matches(rule.Delete!, t.Rel)))
                {
                    return new(rule, tool, t.Arg);
                }
            }
        }
        else if (tool == "Delete" && Str(toolInput, "file_path") is { } deleted)
        {
            var rel = Relative(root, cwd, deleted);
            foreach (var rule in rules.Where(r => r.Delete is not null && Matches(r.Delete, rel)))
            {
                return new(rule, tool, rel);
            }
        }
        else if (EditTools.Contains(tool) && (Str(toolInput, "file_path") ?? Str(toolInput, "notebook_path")) is { } path)
        {
            var rel = Relative(root, cwd, path);
            foreach (var rule in rules.Where(r => r.Edit is not null && Matches(r.Edit, rel)))
            {
                return new(rule, tool, rel);
            }
        }
        return null;
    }

    /// <summary>Hook entry point. Null when the project has no .blackbox/ or nothing matched. Throws on bad input or config; the caller allows the call then.</summary>
    public static GuardOutcome? Check(string stdinJson, string? projectDir, string agent = AgentAdapter.ClaudeCode)
    {
        var raw = JsonNode.Parse(stdinJson) as JsonObject ?? throw new InvalidDataException("hook input is not a JSON object");
        var inputs = AgentAdapter.Normalize(agent, raw, "PreToolUse");
        var root = HookRecorder.FindRoot(projectDir) ?? inputs.Select(i => HookRecorder.FindRoot(Str(i, "cwd"))).FirstOrDefault(r => r is not null);
        if (root is null)
        {
            return null;
        }
        var rules = Rules(BlackboxConfig.Load(Path.Combine(root, HookRecorder.DirName, "config.json")));
        foreach (var input in inputs)
        {
            if (Evaluate(input, root, rules) is { } block)
            {
                return new GuardOutcome(root, block, input);
            }
        }
        return null;
    }

    /// <summary>Appends a ToolBlocked record, so the timeline shows what was stopped. Throws on errors.</summary>
    public static void Record(GuardOutcome outcome, string bbxVersion, DateTimeOffset now)
    {
        var input = outcome.Input.DeepClone().AsObject();
        input["hook_event_name"] = "GuardBlocked";
        input["guard_rule"] = outcome.Block.Rule.Id;
        input["guard_reason"] = outcome.Block.Rule.Reason;
        var blackbox = Path.Combine(outcome.Root, HookRecorder.DirName);
        var config = BlackboxConfig.Load(Path.Combine(blackbox, "config.json"));
        var record = new RecordBuilder(outcome.Root, config, bbxVersion).Build(input, "GuardBlocked", now);
        new SessionStore(blackbox).Append(record, now);
    }

    /// <summary>The message Claude Code shows the agent (stderr with exit code 2).</summary>
    public static string Message(GuardBlock b) =>
        $"bbx guard blocked this {b.Tool} call (rule '{b.Rule.Id}', matched: {b.Target}). {b.Rule.Reason} " +
        "Do not try another way to do the same thing. If it is really needed, tell the user and let them do it themselves.";

    private static bool Matches(IReadOnlyList<Regex> globs, string rel) =>
        globs.Any(g => g.IsMatch(rel) || g.IsMatch(rel.TrimEnd('/') + "/"));

    private static string Relative(string root, string cwd, string path)
    {
        path = path.Trim().Replace('\\', '/');
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

    /// <summary>Paths given to rm, del, Remove-Item or git rm, one per argument.</summary>
    // shortcut: no variable expansion, globbing, "cd" tracking or find -delete; a determined agent gets past this anyway.
    public static IEnumerable<string> DeleteTargets(string command)
    {
        foreach (var tokens in Segments(command))
        {
            var i = tokens.Count > 0 && tokens[0] == "sudo" ? 1 : 0;
            if (i >= tokens.Count)
            {
                continue;
            }
            var verb = tokens[i].ToLowerInvariant();
            if (verb == "git" && i + 1 < tokens.Count && tokens[i + 1] == "rm")
            {
                i++;
            }
            else if (!DeleteVerbs.Contains(verb))
            {
                continue;
            }
            foreach (var t in tokens.Skip(i + 1).Where(t => !t.StartsWith('-') && t.Length > 0))
            {
                yield return t;
            }
        }
    }

    /// <summary>Splits a command line into simple commands (on ; &amp; | and newlines outside quotes), each as its words with quotes removed.</summary>
    private static List<List<string>> Segments(string s)
    {
        var segments = new List<List<string>>();
        var tokens = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        var any = false;
        void EndToken()
        {
            if (sb.Length > 0 || any)
            {
                tokens.Add(sb.ToString());
            }
            sb.Clear();
            any = false;
        }
        void EndSegment()
        {
            EndToken();
            if (tokens.Count > 0)
            {
                segments.Add(tokens);
            }
            tokens = [];
        }
        foreach (var ch in s)
        {
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                any = true;
            }
            else if (ch is ';' or '&' or '|' or '\n')
            {
                EndSegment();
            }
            else if (char.IsWhiteSpace(ch))
            {
                EndToken();
            }
            else
            {
                sb.Append(ch);
            }
        }
        EndSegment();
        return segments;
    }

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // "git [opts] push ... --force | -f | -uf | +refspec", but not --force-with-lease. Stays within one command segment.
    [GeneratedRegex(@"(?:^|[\s;&|(])git\b[^;&|\n]*?\spush\b[^;&|\n]*?\s(?:--force(?![\w-])|-[a-zA-Z]*f[a-zA-Z]*(?![\w-])|\+[\w./-])")]
    private static partial Regex ForcePush();

    [GeneratedRegex(@"(?:^|[\s;&|(])dotnet[\s-]ef\s+database\s+drop\b")]
    private static partial Regex DropDatabase();

    [GeneratedRegex(@"'[^']*'|""(?:[^""\\]|\\.)*""")]
    private static partial Regex Quoted();
}
