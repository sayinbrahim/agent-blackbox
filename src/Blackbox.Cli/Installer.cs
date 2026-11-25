using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Blackbox.Core;

namespace Blackbox.Cli;

/// <summary>bbx init: creates .blackbox/ and merges hooks into the agent's hook config without touching anything else.</summary>
public static partial class Installer
{
    public static readonly string[] Events = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "SessionEnd"];
    private static readonly HashSet<string> ToolEvents = ["PreToolUse", "PostToolUse", "PostToolUseFailure"];
    private static readonly string[] CodexEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop", "SessionEnd"];
    private static readonly string[] CursorEvents = ["sessionStart", "beforeSubmitPrompt", "afterFileEdit", "afterShellExecution", "stop", "sessionEnd"];
    private static readonly string[] CopilotEvents = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "SessionEnd"];
    private const int HookTimeoutSeconds = 5;
    public const string GuardMatcher = "Bash|PowerShell|Edit|Write|MultiEdit|NotebookEdit";

    /// <summary>Hook config files bbx may write, relative to the project root. Claude Code's local file is chosen by --local.</summary>
    public static string ConfigPath(string root, string agent, bool local) => agent switch
    {
        "codex" => Path.Combine(root, ".codex", "hooks.json"),
        "cursor" => Path.Combine(root, ".cursor", "hooks.json"),
        "copilot" => Path.Combine(root, ".github", "hooks", "agent-blackbox.json"),
        _ => Path.Combine(root, ".claude", local ? "settings.local.json" : "settings.json"),
    };

    public static List<string> Install(string root, bool local, bool makePrivate, string? processPath, bool guard = false, string agent = AgentAdapter.ClaudeCode)
    {
        if (!AgentAdapter.Names.Contains(agent))
        {
            throw new InvalidOperationException($"Unknown agent '{agent}'. Use one of: {string.Join(", ", AgentAdapter.Names)}.");
        }
        var log = new List<string>();
        var blackbox = Path.Combine(root, HookRecorder.DirName);
        Directory.CreateDirectory(Path.Combine(blackbox, "sessions"));
        WriteIfMissing(Path.Combine(blackbox, "config.json"), BlackboxConfig.DefaultJson, log);
        WriteIfMissing(Path.Combine(blackbox, ".gitignore"), "errors.log\n.lock\n", log);

        var settingsPath = ConfigPath(root, agent, local);
        string prefix;
        if (local)
        {
            if (processPath is null)
            {
                throw new InvalidOperationException("Cannot determine the path of the bbx binary.");
            }
            // Forward slashes work in both Git Bash and PowerShell, the two shells Claude Code uses on Windows.
            prefix = "\"" + processPath.Replace('\\', '/') + "\"";
            EnsureGitignoreLine(root, Rel(root, settingsPath), log); // holds a machine-specific path
        }
        else
        {
            prefix = "bbx";
            if (!OnPath())
            {
                log.Add("Warning: 'bbx' is not on PATH. Hooks will show 'hook error' until it is. Use 'bbx init --local' to use this binary's absolute path instead.");
            }
        }

        var settings = LoadSettings(settingsPath);
        var hooks = settings["hooks"] as JsonObject ?? new JsonObject();
        settings["hooks"] = hooks;
        var suffix = agent == AgentAdapter.ClaudeCode ? "" : $" --agent {agent}";
        string Hook(string evt) => $"{prefix} hook {evt}{suffix}";
        var guardCommand = $"{prefix} guard{suffix}";

        var added = 0;
        var guardAdded = 0;
        switch (agent)
        {
            case "codex":
                added += CodexEvents.Sum(evt => AddGroup(hooks, evt, null, Hook(evt), guard: false));
                guardAdded += guard ? AddGroup(hooks, "PreToolUse", "^(Bash|apply_patch)$", guardCommand, guard: true) : 0;
                break;
            case "cursor":
                settings["version"] ??= 1;
                added += CursorEvents.Sum(evt => AddEntry(hooks, evt, CursorEntry(Hook(evt), null), guard: false));
                if (guard)
                {
                    guardAdded += AddEntry(hooks, "beforeShellExecution", CursorEntry(guardCommand, null), guard: true);
                    guardAdded += AddEntry(hooks, "preToolUse", CursorEntry(guardCommand, "Write|Delete"), guard: true);
                }
                break;
            case "copilot":
                settings["version"] ??= 1;
                added += CopilotEvents.Sum(evt => AddEntry(hooks, evt, CopilotEntry(Hook(evt)), guard: false));
                guardAdded += guard ? AddEntry(hooks, "PreToolUse", CopilotEntry(guardCommand), guard: true) : 0;
                break;
            default:
                added += Events.Sum(evt => AddGroup(hooks, evt, ToolEvents.Contains(evt) ? "*" : null, Hook(evt), guard: false));
                guardAdded += guard ? AddGroup(hooks, "PreToolUse", GuardMatcher, guardCommand, guard: true) : 0;
                break;
        }
        if (guardAdded > 0)
        {
            log.Add("Guard on: force pushes, deleted migrations and edits to recordings are blocked. Add rules under \"guard\" in .blackbox/config.json.");
        }

        if (added + guardAdded > 0)
        {
            Save(settingsPath, settings, log);
            log.Add($"Added {added + guardAdded} hook(s) to {Rel(root, settingsPath)}.");
        }
        else
        {
            log.Add($"Hooks already installed in {Rel(root, settingsPath)}. Nothing changed.");
        }

        if (makePrivate)
        {
            EnsureGitignoreLine(root, "/.blackbox/", log);
        }
        else
        {
            log.Add(".blackbox/ will be committed with your code so the team shares provenance. Use --private to keep it local.");
        }
        log.Add(agent switch
        {
            "codex" => "Codex runs new hooks only after you trust them: run /hooks in Codex and trust the bbx hooks. Codex support is experimental.",
            "cursor" => "Recording starts with the next Cursor agent session in this project. Cursor support is experimental.",
            "copilot" => "Recording starts with the next Copilot CLI session in this repository. Copilot support is experimental.",
            _ => "Recording starts with the next Claude Code session in this project.",
        });
        return log;
    }

    /// <summary>Claude Code / Codex shape: event -> [{ matcher?, hooks: [{ type, command, timeout }] }]. Returns 1 if added.</summary>
    private static int AddGroup(JsonObject hooks, string evt, string? matcher, string command, bool guard)
    {
        var groups = hooks[evt] as JsonArray ?? new JsonArray();
        hooks[evt] = groups;
        if (groups.OfType<JsonObject>().Any(g => (g["hooks"] as JsonArray ?? []).Any(h => IsOurs(h) && IsGuard(h) == guard)))
        {
            return 0; // idempotent
        }
        var group = new JsonObject();
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }
        group["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = HookTimeoutSeconds });
        groups.Add((JsonNode)group);
        return 1;
    }

    /// <summary>Cursor / Copilot shape: event -> [{ command, ... }]. Returns 1 if added.</summary>
    private static int AddEntry(JsonObject hooks, string evt, JsonObject entry, bool guard)
    {
        var list = hooks[evt] as JsonArray ?? new JsonArray();
        hooks[evt] = list;
        if (list.Any(h => IsOurs(h) && IsGuard(h) == guard))
        {
            return 0;
        }
        list.Add((JsonNode)entry);
        return 1;
    }

    private static JsonObject CursorEntry(string command, string? matcher)
    {
        // Cursor runs hooks through PowerShell on Windows, where a quoted path needs the call operator.
        var e = new JsonObject { ["command"] = OperatingSystem.IsWindows() && command.StartsWith('"') ? "& " + command : command, ["timeout"] = HookTimeoutSeconds };
        if (matcher is not null)
        {
            e["matcher"] = matcher;
        }
        return e;
    }

    private static JsonObject CopilotEntry(string command) => new()
    {
        ["type"] = "command",
        ["bash"] = command,
        ["powershell"] = command.StartsWith('"') ? "& " + command : command,
        ["timeoutSec"] = HookTimeoutSeconds,
    };

    public static List<string> Uninstall(string root)
    {
        var log = new List<string>();
        var files = new[]
        {
            ConfigPath(root, AgentAdapter.ClaudeCode, false), ConfigPath(root, AgentAdapter.ClaudeCode, true),
            ConfigPath(root, "codex", false), ConfigPath(root, "cursor", false), ConfigPath(root, "copilot", false),
        };
        foreach (var path in files.Where(File.Exists))
        {
            var settings = LoadSettings(path);
            if (settings["hooks"] is not JsonObject hooks)
            {
                continue;
            }
            var removed = 0;
            foreach (var evt in hooks.Select(p => p.Key).ToList())
            {
                if (hooks[evt] is not JsonArray entries)
                {
                    continue;
                }
                foreach (var entry in entries.OfType<JsonObject>().ToList())
                {
                    if (entry["hooks"] is JsonArray list)
                    {
                        // grouped (Claude Code, Codex)
                        foreach (var h in list.Where(IsOurs).ToList())
                        {
                            list.Remove(h);
                            removed++;
                        }
                        if (list.Count == 0)
                        {
                            entries.Remove(entry);
                        }
                    }
                    else if (IsOurs(entry))
                    {
                        // flat (Cursor, Copilot)
                        entries.Remove(entry);
                        removed++;
                    }
                }
                if (entries.Count == 0)
                {
                    hooks.Remove(evt);
                }
            }
            if (hooks.Count == 0)
            {
                settings.Remove("hooks");
            }
            if (removed > 0)
            {
                Save(path, settings, log);
                log.Add($"Removed {removed} bbx hook(s) from {Rel(root, path)}.");
            }
        }
        if (log.Count == 0)
        {
            log.Add("No bbx hooks found. Nothing changed.");
        }
        log.Add(".blackbox/ was left in place.");
        return log;
    }

    public static bool IsOurs(JsonNode? hook) => CommandOf(hook) is { } cmd && OurCommand().IsMatch(cmd);

    public static bool IsGuard(JsonNode? hook) => CommandOf(hook) is { } cmd && GuardCommand().IsMatch(cmd);

    private static string? CommandOf(JsonNode? hook) =>
        (hook?["command"] ?? hook?["bash"]) is JsonValue v && v.TryGetValue<string>(out var cmd) ? cmd : null;

    [GeneratedRegex(@"bbx(?:\.exe)?""?\s+(?:hook\s+\w+|guard)(?:\s+--agent\s+[\w-]+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OurCommand();

    [GeneratedRegex(@"bbx(?:\.exe)?""?\s+guard(?:\s+--agent\s+[\w-]+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GuardCommand();

    private static JsonObject LoadSettings(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonNode.Parse(text, documentOptions: options) as JsonObject
            ?? throw new InvalidDataException($"{path} is not a JSON object. Not touching it.");
    }

    private static void Save(string path, JsonObject settings, List<string> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bbx-backup", overwrite: true);
            log.Add($"Backed up {Path.GetFileName(path)} to {Path.GetFileName(path)}.bbx-backup.");
        }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            settings.WriteTo(w);
        }
        File.WriteAllText(path, Encoding.UTF8.GetString(ms.ToArray()) + "\n", new UTF8Encoding(false));
    }

    private static void WriteIfMissing(string path, string content, List<string> log)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
            log.Add($"Created {HookRecorder.DirName}/{Path.GetFileName(path)}.");
        }
    }

    private static void EnsureGitignoreLine(string root, string line, List<string> log)
    {
        var path = Path.Combine(root, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (existing.Any(l => l.Trim() == line))
        {
            return;
        }
        var prefix = existing.Length > 0 && !File.ReadAllText(path).EndsWith('\n') ? "\n" : "";
        File.AppendAllText(path, prefix + line + "\n");
        log.Add($"Added {line} to .gitignore.");
    }

    private static bool OnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => File.Exists(Path.Combine(d, "bbx")) || File.Exists(Path.Combine(d, "bbx.exe")));

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
