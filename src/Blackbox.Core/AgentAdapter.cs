using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blackbox.Core;

/// <summary>
/// Turns hook payloads from other agents into the Claude Code shape the recorder and the guard understand.
/// Field names follow each agent's published hook docs (Codex, Cursor, Copilot CLI, 2026-11). Shapes the docs leave open
/// are read defensively, and what an agent does not report (a shell exit code) is marked unknown instead of guessed.
/// </summary>
public static partial class AgentAdapter
{
    public const string ClaudeCode = "claude-code";
    public static readonly string[] Names = [ClaudeCode, "codex", "cursor", "copilot"];

    /// <summary>Zero or more Claude Code-shaped payloads (an apply_patch over three files gives three).</summary>
    public static IReadOnlyList<JsonObject> Normalize(string agent, JsonObject input, string eventArg) => agent switch
    {
        ClaudeCode => [input],
        "codex" => Codex(input),
        "cursor" => Cursor(input, eventArg),
        "copilot" => Copilot(input, eventArg),
        _ => throw new InvalidDataException($"unknown agent '{agent}' (expected {string.Join(", ", Names)})"),
    };

    // ---------------- Codex: Claude-compatible events. Edits are apply_patch, shell output is the model-facing text.

    private static List<JsonObject> Codex(JsonObject input)
    {
        Tag(input, "codex");
        var tool = S(input, "tool_name");
        if (tool == "apply_patch" && S(input["tool_input"] as JsonObject, "command") is { } patch)
        {
            return [.. ParsePatch(patch).Select(f => FileEvent(input, f.Op, f.Path, f.Old, f.New, "apply_patch"))];
        }
        if (tool == "Bash" && S(input, "hook_event_name") == "PostToolUse")
        {
            Shell(input, TextOf(input["tool_response"]), failed: false);
        }
        return [input];
    }

    private sealed record PatchFile(string Op, string Path, string Old, string New);

    /// <summary>Parses the apply_patch format: Add/Update/Delete File sections with +/-/space lines. Context lines are not counted as written.</summary>
    private static List<PatchFile> ParsePatch(string patch)
    {
        var files = new List<PatchFile>();
        string? op = null, path = null;
        StringBuilder old = new(), @new = new();
        void Flush()
        {
            if (op is not null && path is not null)
            {
                files.Add(new(op, path, old.ToString(), @new.ToString()));
            }
            op = path = null;
            old.Clear();
            @new.Clear();
        }
        foreach (var raw in patch.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                Flush();
                (op, path) = ("create", raw[14..].Trim());
            }
            else if (raw.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                Flush();
                (op, path) = ("edit", raw[17..].Trim());
            }
            else if (raw.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                Flush();
                (op, path) = ("delete", raw[17..].Trim());
            }
            else if (raw.StartsWith("*** Move to: ", StringComparison.Ordinal) && op is not null)
            {
                path = raw[13..].Trim();
            }
            else if (raw.StartsWith("***", StringComparison.Ordinal) || raw.StartsWith("@@", StringComparison.Ordinal))
            {
                // Begin/End Patch, End of File, hunk headers
            }
            else if (raw.StartsWith('+'))
            {
                @new.Append(raw.AsSpan(1)).Append('\n');
            }
            else if (raw.StartsWith('-'))
            {
                old.Append(raw.AsSpan(1)).Append('\n');
            }
        }
        Flush();
        return files;
    }

    // ---------------- Cursor: own event names; edits arrive in afterFileEdit, shell runs in after/beforeShellExecution.

    private static List<JsonObject> Cursor(JsonObject input, string eventArg)
    {
        var evt = S(input, "hook_event_name") ?? eventArg;
        var session = S(input, "conversation_id") ?? S(input, "session_id");
        var cwd = S(input, "cwd") ?? (input["workspace_roots"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
        JsonObject Base(string claudeEvent)
        {
            var o = new JsonObject { ["session_id"] = session, ["cwd"] = cwd, ["hook_event_name"] = claudeEvent, ["model"] = S(input, "model") };
            Tag(o, "cursor");
            return o;
        }
        switch (evt)
        {
            case "sessionStart":
            {
                var o = Base("SessionStart");
                o["source"] = "startup";
                return [o];
            }
            case "beforeSubmitPrompt":
            {
                var o = Base("UserPromptSubmit");
                o["prompt"] = S(input, "prompt");
                return [o];
            }
            case "afterFileEdit":
            {
                var path = S(input, "file_path") ?? "";
                return [.. (input["edits"] as JsonArray ?? []).OfType<JsonObject>().Select(e =>
                    FileEvent(Base("PostToolUse"), "edit", path, S(e, "old_string") ?? "", S(e, "new_string") ?? "", "afterFileEdit"))];
            }
            case "afterShellExecution":
            {
                var o = Base("PostToolUse");
                o["tool_name"] = "Bash";
                o["tool_input"] = new JsonObject { ["command"] = S(input, "command") };
                o["duration_ms"] = input["duration"]?.DeepClone();
                Shell(o, S(input, "output") ?? "", failed: false);
                return [o];
            }
            case "beforeShellExecution":
            {
                var o = Base("PreToolUse");
                o["tool_name"] = "Bash";
                o["tool_input"] = new JsonObject { ["command"] = S(input, "command") };
                return [o];
            }
            case "preToolUse":
            {
                var o = Base("PreToolUse");
                var tool = S(input, "tool_name");
                var args = Args(input["tool_input"]);
                o["tool_use_id"] = S(input, "tool_use_id");
                o["tool_name"] = tool switch { "Shell" => "Bash", _ => tool };
                o["tool_input"] = tool == "Shell" ? args : FileArgs(args);
                return [o];
            }
            case "stop":
                return [Base("Stop")];
            case "sessionEnd":
            {
                var o = Base("SessionEnd");
                o["reason"] = S(input, "reason");
                return [o];
            }
            default:
                return [];
        }
    }

    // ---------------- Copilot CLI: PascalCase hook names give snake_case fields, camelCase names give camelCase fields.

    private static readonly Dictionary<string, string> CopilotEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sessionStart"] = "SessionStart", ["sessionEnd"] = "SessionEnd", ["userPromptSubmitted"] = "UserPromptSubmit",
        ["UserPromptSubmit"] = "UserPromptSubmit", ["preToolUse"] = "PreToolUse", ["postToolUse"] = "PostToolUse",
        ["postToolUseFailure"] = "PostToolUseFailure", ["agentStop"] = "Stop", ["Stop"] = "Stop",
    };

    private static readonly Dictionary<string, string> CopilotTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bash"] = "Bash", ["powershell"] = "PowerShell", ["create"] = "Write", ["write"] = "Write", ["edit"] = "Edit",
        ["str_replace_editor"] = "Edit", ["view"] = "Read", ["read"] = "Read",
    };

    private static List<JsonObject> Copilot(JsonObject input, string eventArg)
    {
        var name = S(input, "hook_event_name") ?? eventArg;
        var evt = CopilotEvents.TryGetValue(name, out var e) ? e : name;
        var o = new JsonObject
        {
            ["session_id"] = S(input, "session_id") ?? S(input, "sessionId"),
            ["cwd"] = S(input, "cwd"),
            ["hook_event_name"] = evt,
        };
        Tag(o, "copilot");
        foreach (var (from, to) in new[] { ("source", "source"), ("prompt", "prompt"), ("reason", "reason"), ("error", "error"), ("tool_use_id", "tool_use_id"), ("toolUseId", "tool_use_id") })
        {
            if (input[from] is { } v)
            {
                o[to] = v.DeepClone();
            }
        }
        var rawTool = S(input, "tool_name") ?? S(input, "toolName");
        if (rawTool is null)
        {
            return [o];
        }
        var tool = CopilotTools.TryGetValue(rawTool, out var t) ? t : rawTool;
        var args = Args(input["tool_input"] ?? input["toolArgs"]);
        o["tool_name"] = tool;
        o["tool_input"] = tool is "Bash" or "PowerShell" ? args : FileArgs(args);
        if (tool == "Write" && evt == "PostToolUse")
        {
            o["tool_response"] = new JsonObject { ["type"] = "create" };
        }
        if (tool is "Bash" or "PowerShell" && evt == "PostToolUse")
        {
            var result = input["tool_result"] as JsonObject ?? input["toolResult"] as JsonObject;
            var text = S(result, "text_result_for_llm") ?? S(result, "textResultForLlm") ?? "";
            var type = S(result, "result_type") ?? S(result, "resultType");
            Shell(o, text, failed: type is "failure" or "error" or "denied");
        }
        return [o];
    }

    // ---------------- shared

    /// <summary>A Claude Code Edit, Write or Delete payload for one file.</summary>
    private static JsonObject FileEvent(JsonObject template, string op, string path, string old, string @new, string agentTool)
    {
        var o = template.DeepClone().AsObject();
        o["agent_tool"] = agentTool;
        switch (op)
        {
            case "create":
                o["tool_name"] = "Write";
                o["tool_input"] = new JsonObject { ["file_path"] = path, ["content"] = @new };
                o["tool_response"] = new JsonObject { ["type"] = "create" };
                break;
            case "delete":
                o["tool_name"] = "Delete";
                o["tool_input"] = new JsonObject { ["file_path"] = path };
                o.Remove("tool_response");
                break;
            default:
                o["tool_name"] = "Edit";
                o["tool_input"] = new JsonObject { ["file_path"] = path, ["old_string"] = old, ["new_string"] = @new };
                o.Remove("tool_response");
                break;
        }
        return o;
    }

    /// <summary>Sets the shell result from output text. The exit code is read from the text when the agent puts it there, else marked unknown.</summary>
    private static void Shell(JsonObject o, string output, bool failed)
    {
        o["tool_response"] = new JsonObject { ["stdout"] = output };
        int? code = ExitCode().Match(output) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var c) ? c : null;
        if (code is not null && code != 0 || failed)
        {
            o["hook_event_name"] = "PostToolUseFailure";
            o["error"] = $"Exit code {code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}\n{output}";
        }
        else if (code is null)
        {
            o["bbx_exit_unknown"] = true;
        }
    }

    /// <summary>Tool arguments as an object; some agents send them as a JSON string.</summary>
    private static JsonObject Args(JsonNode? n)
    {
        if (n is JsonObject obj)
        {
            return obj.DeepClone().AsObject();
        }
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try
            {
                return JsonNode.Parse(s) as JsonObject ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                return new JsonObject { ["raw"] = s };
            }
        }
        return new JsonObject();
    }

    /// <summary>Renames file-tool arguments to Claude Code's: file_path, old_string, new_string, content.</summary>
    private static JsonObject FileArgs(JsonObject args)
    {
        foreach (var (from, to) in new[] { ("path", "file_path"), ("filePath", "file_path"), ("old_str", "old_string"), ("new_str", "new_string"), ("file_text", "content") })
        {
            if (args[from] is { } v && args[to] is null)
            {
                args.Remove(from);
                args[to] = v;
            }
        }
        return args;
    }

    private static void Tag(JsonObject o, string agent) => o["bbx_agent"] = agent;

    private static string TextOf(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonObject o => S(o, "output") ?? S(o, "stdout") ?? o.ToJsonString(),
        null => "",
        _ => n.ToJsonString(),
    };

    private static string? S(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // "Exit code: 1" (Codex), "<exited with exit code 1>" (Copilot CLI)
    [GeneratedRegex(@"(?im)(?:^\s*Exit code:?\s*|<exited with exit code\s+)(-?\d+)")]
    private static partial Regex ExitCode();
}
