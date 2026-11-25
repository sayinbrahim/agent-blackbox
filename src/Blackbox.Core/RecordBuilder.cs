using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

/// <summary>Turns one Claude Code hook payload into a record (without seq/prev/hash; the store adds those).</summary>
public sealed class RecordBuilder(string root, BlackboxConfig config, string bbxVersion)
{
    public const int SchemaVersion = 1;

    // Fields we map explicitly. Absolute paths with user names (transcript_path, scratchpad_dir) are dropped on purpose.
    private static readonly HashSet<string> Known =
    [
        "session_id", "transcript_path", "cwd", "scratchpad_dir", "prompt_id", "permission_mode", "effort", "hook_event_name",
        "tool_name", "tool_input", "tool_response", "tool_use_id", "duration_ms", "prompt", "source", "model", "reason",
        "stop_hook_active", "last_assistant_message", "error", "is_interrupt", "agent_id", "agent_type", "session_title",
        "background_tasks", "session_crons", "seconds_since_last_response", "context_tokens", "prompt_cache_likely_expired",
        "estimated_cache_write_usd", "error_details", "agent_transcript_path", "mcp_server", "guard_rule", "guard_reason", "bbx_agent", "bbx_exit_unknown", "agent_tool",
    ];

    private readonly Redactor? _redactor = config.RedactionEnabled ? new Redactor(config.ExtraPatterns, Redactor.PersonalLiterals()) : null;
    private readonly CommandClassifier _classifier = new(config);

    public JsonObject Build(JsonObject input, string eventArg, DateTimeOffset now)
    {
        var evt = Str(input, "hook_event_name") ?? eventArg;
        var tool = Str(input, "tool_name");
        var data = new JsonObject();
        string? kind = null;
        var actor = "agent";

        switch (evt)
        {
            case "SessionStart":
                kind = "SessionStarted";
                actor = "system";
                Copy(input, data, "source", "model");
                data["cwd"] = Rel(Str(input, "cwd") ?? root, out _);
                var (branch, head) = GitInfo.Read(root);
                data["git_branch"] = branch;
                data["git_head"] = head;
                data["bbx_version"] = bbxVersion;
                if (Str(input, "bbx_agent") is { } agent)
                {
                    data["agent"] = agent;
                }
                break;
            case "SessionEnd":
                kind = "SessionEnded";
                actor = "system";
                Copy(input, data, "reason");
                break;
            case "UserPromptSubmit" when IsNotificationText(Str(input, "prompt")):
                kind = "TaskNotification";
                actor = "system";
                data["summary"] = NotificationSummary(Str(input, "prompt")!);
                break;
            case "UserPromptSubmit":
                kind = "PromptSubmitted";
                actor = "human";
                Copy(input, data, "prompt_id");
                var prompt = Str(input, "prompt") ?? "";
                if (config.RecordPrompts == "full")
                {
                    data["prompt"] = Truncate(prompt, 16 * 1024);
                }
                else if (config.RecordPrompts == "hash")
                {
                    data["prompt_hash"] = "sha256:" + HashChain.Sha256Hex(prompt);
                }
                break;
            case "Stop":
                kind = "AgentStopped";
                Copy(input, data, "stop_hook_active");
                if (Str(input, "last_assistant_message") is { } msg)
                {
                    data["message"] = Truncate(msg, 2048);
                }
                break;
            case "PreToolUse":
                Copy(input, data, "tool_use_id");
                data["input"] = SummarizeInput(input["tool_input"] as JsonObject);
                break;
            case "GuardBlocked":
                kind = "ToolBlocked";
                Copy(input, data, "tool_use_id");
                data["rule"] = Str(input, "guard_rule");
                data["reason"] = Str(input, "guard_reason");
                data["input"] = SummarizeInput(input["tool_input"] as JsonObject);
                break;
            case "PostToolUse":
            case "PostToolUseFailure":
                kind = ToolResult(input, tool ?? "", evt == "PostToolUseFailure", data);
                break;
            default:
                actor = "system";
                break;
        }
        if (Str(input, "agent_type") is { } agentType)
        {
            data["agent_type"] = agentType; // the action came from a subagent
        }

        var record = new JsonObject
        {
            ["v"] = SchemaVersion,
            ["ts"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["session"] = Str(input, "session_id"),
            ["event"] = evt,
            ["actor"] = actor,
            ["data"] = data,
            ["raw_extra"] = RawExtra(input),
        };
        if (kind is not null)
        {
            record["kind"] = kind;
        }
        if (tool is not null)
        {
            record["tool"] = tool;
        }
        _redactor?.RedactTree(record);
        return record;
    }

    private string? ToolResult(JsonObject input, string tool, bool failed, JsonObject data)
    {
        var toolInput = input["tool_input"] as JsonObject;
        var response = input["tool_response"];
        Copy(input, data, "tool_use_id", "duration_ms", "agent_tool");
        var exitUnknown = input["bbx_exit_unknown"] is JsonValue u && u.TryGetValue<bool>(out var unknown) && unknown;
        data["success"] = exitUnknown ? null : !failed; // the agent did not report how the command ended
        data["input"] = SummarizeInput(toolInput);
        if (failed)
        {
            var error = Str(input, "error") ?? "";
            data["error"] = Tail(error, 2048);
            Copy(input, data, "is_interrupt");
            if (error.StartsWith("Exit code ", StringComparison.Ordinal)
                && int.TryParse(error.AsSpan(10).TrimEnd().ToString().Split('\n')[0], CultureInfo.InvariantCulture, out var code))
            {
                data["exit_code"] = code;
            }
        }

        switch (tool)
        {
            case "Read":
                return "FileRead";
            case "Delete":
                data["file"] = Rel(Str(toolInput, "file_path") ?? "", out _);
                return "FileDeleted";
            case "Edit" or "Write" or "NotebookEdit":
                if (failed)
                {
                    return "FileModified";
                }
                return FileChange(tool, toolInput, response as JsonObject, data) == "create" ? "FileCreated" : "FileModified";
            case "Bash" or "PowerShell":
                var command = Str(toolInput, "command") ?? "";
                if (!failed)
                {
                    if (!exitUnknown)
                    {
                        data["exit_code"] = 0;
                    }
                    if (response is JsonObject r)
                    {
                        data["output_tail"] = Tail(Str(r, "stdout") ?? "", 1024);
                        Copy(r, data, "interrupted");
                    }
                }
                return _classifier.Classify(command) switch
                {
                    CommandKind.Test => "TestCompleted",
                    CommandKind.Build => "BuildCompleted",
                    CommandKind.Delete => "FileDeleted",
                    _ => "CommandExecuted",
                };
            default:
                return null;
        }
    }

    /// <returns>The operation: create, update or edit.</returns>
    private string FileChange(string tool, JsonObject? toolInput, JsonObject? response, JsonObject data)
    {
        var abs = Str(toolInput, "file_path") ?? Str(toolInput, "notebook_path") ?? Str(response, "filePath") ?? "";
        var op = tool == "Write" ? Str(response, "type") ?? "update" : "edit";
        var rel = Rel(abs, out var outside);
        data["file"] = rel;
        data["op"] = op;
        if (outside)
        {
            data["outside_project"] = true;
        }
        if (config.IsIgnored(rel))
        {
            data["ignored"] = true; // only "file changed": no diff, no hashes
            return op;
        }

        if (Str(response, "originalFile") is { } original)
        {
            data["before_hash"] = "sha256:" + HashChain.Sha256Hex(original);
        }
        if (abs.Length > 0 && File.Exists(abs))
        {
            data["after_hash"] = "sha256:" + HashChain.Sha256Hex(File.ReadAllBytes(abs));
        }

        var (diff, added, removed) = Diff(toolInput, response, op);
        data["added_lines"] = added.Count;
        data["removed_lines"] = removed;
        data["added_line_hashes"] = new JsonArray([.. added.Select(LineHasher.Hash).OfType<string>().Distinct().Select(h => (JsonNode)h)]);
        data["diff"] = Truncate(diff, config.MaxDiffKb * 1024);
        return op;
    }

    private static (string Diff, List<string> Added, int Removed) Diff(JsonObject? toolInput, JsonObject? response, string op)
    {
        var sb = new StringBuilder();
        var added = new List<string>();
        var removed = 0;
        if (response?["structuredPatch"] is JsonArray { Count: > 0 } hunks)
        {
            foreach (var hunk in hunks.OfType<JsonObject>())
            {
                sb.Append(CultureInfo.InvariantCulture, $"@@ -{hunk["oldStart"]},{hunk["oldLines"]} +{hunk["newStart"]},{hunk["newLines"]} @@\n");
                foreach (var line in (hunk["lines"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? ""))
                {
                    sb.Append(line).Append('\n');
                    if (line.StartsWith('+'))
                    {
                        added.Add(line[1..]);
                    }
                    else if (line.StartsWith('-'))
                    {
                        removed++;
                    }
                }
            }
            return (sb.ToString(), added, removed);
        }

        // No patch (new file, or a tool that doesn't return one): fall back to the input text.
        var newText = op == "create" ? Str(toolInput, "content") ?? Str(response, "content") : Str(toolInput, "new_string") ?? Str(toolInput, "new_source") ?? Str(toolInput, "content");
        foreach (var line in SplitLines(Str(toolInput, "old_string")))
        {
            sb.Append('-').Append(line).Append('\n');
            removed++;
        }
        foreach (var line in SplitLines(newText))
        {
            sb.Append('+').Append(line).Append('\n');
            added.Add(line);
        }
        return (sb.ToString(), added, removed);
    }

    private static IEnumerable<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    private JsonObject SummarizeInput(JsonObject? toolInput)
    {
        var s = new JsonObject();
        if (toolInput is null)
        {
            return s;
        }
        foreach (var key in new[] { "command", "description", "pattern", "glob", "url", "query", "subagent_type", "run_in_background" })
        {
            if (toolInput[key] is JsonValue v)
            {
                s[key] = v.TryGetValue<string>(out var str) ? Truncate(str, 2048) : v.DeepClone();
            }
        }
        foreach (var key in new[] { "file_path", "notebook_path", "path" })
        {
            if (Str(toolInput, key) is { } p)
            {
                s[key == "path" ? "path" : "file"] = Rel(p, out _);
            }
        }
        return s;
    }

    private JsonObject RawExtra(JsonObject input)
    {
        var extra = new JsonObject();
        foreach (var (key, value) in input)
        {
            if (!Known.Contains(key))
            {
                extra[key] = value is JsonValue v && v.TryGetValue<string>(out var s) ? Truncate(s, 1024) : value?.DeepClone();
            }
        }
        return extra;
    }

    /// <summary>Repo-relative path with '/' separators. Paths outside the project keep only the file name (no user directories in the log).</summary>
    public string Rel(string path, out bool outside)
    {
        outside = false;
        if (path.Length == 0)
        {
            return path;
        }
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        var rel = Path.GetRelativePath(root, full);
        if (rel == ".")
        {
            return ".";
        }
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            outside = true;
            return "<outside>/" + Path.GetFileName(full);
        }
        return rel.Replace('\\', '/');
    }

    private static void Copy(JsonObject? from, JsonObject to, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (from?[key] is { } v)
            {
                to[key] = v.DeepClone();
            }
        }
    }

    private static string? Str(JsonObject? obj, string key) =>
        obj?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"…[truncated {s.Length - max} chars]";

    /// <summary>Claude Code delivers background task results as a UserPromptSubmit wrapped in &lt;task-notification&gt;.</summary>
    public static bool IsNotificationText(string? prompt) =>
        prompt is not null && prompt.AsSpan().TrimStart().StartsWith("<task-notification>", StringComparison.Ordinal);

    /// <summary>The &lt;summary&gt; of a task notification, or a fixed label when it has none.</summary>
    public static string NotificationSummary(string prompt)
    {
        var start = prompt.IndexOf("<summary>", StringComparison.Ordinal);
        var end = start < 0 ? -1 : prompt.IndexOf("</summary>", start, StringComparison.Ordinal);
        return end < 0 ? "task finished" : Truncate(prompt[(start + "<summary>".Length)..end].Trim(), 300);
    }

    private static string Tail(string s, int max) =>
        s.Length <= max ? s : $"[…{s.Length - max} chars] " + s[^max..];
}
