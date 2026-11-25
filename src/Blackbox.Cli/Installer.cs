using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Blackbox.Core;

namespace Blackbox.Cli;

/// <summary>bbx init: creates .blackbox/ and merges hooks into Claude Code settings without touching anything else.</summary>
public static partial class Installer
{
    public static readonly string[] Events = ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "Stop", "SessionEnd"];
    private static readonly HashSet<string> ToolEvents = ["PreToolUse", "PostToolUse", "PostToolUseFailure"];
    private const int HookTimeoutSeconds = 5;
    public const string GuardMatcher = "Bash|PowerShell|Edit|Write|MultiEdit|NotebookEdit";

    public static List<string> Install(string root, bool local, bool makePrivate, string? processPath, bool guard = false)
    {
        var log = new List<string>();
        var blackbox = Path.Combine(root, HookRecorder.DirName);
        Directory.CreateDirectory(Path.Combine(blackbox, "sessions"));
        WriteIfMissing(Path.Combine(blackbox, "config.json"), BlackboxConfig.DefaultJson, log);
        WriteIfMissing(Path.Combine(blackbox, ".gitignore"), "errors.log\n.lock\n", log);

        string prefix;
        if (local)
        {
            if (processPath is null)
            {
                throw new InvalidOperationException("Cannot determine the path of the bbx binary.");
            }
            // Forward slashes work in both Git Bash and PowerShell, the two shells Claude Code uses on Windows.
            prefix = "\"" + processPath.Replace('\\', '/') + "\"";
            EnsureGitignoreLine(root, ".claude/settings.local.json", log); // holds a machine-specific path
        }
        else
        {
            prefix = "bbx";
            if (!OnPath())
            {
                log.Add("Warning: 'bbx' is not on PATH. Hooks will show 'hook error' until it is. Use 'bbx init --local' to use this binary's absolute path instead.");
            }
        }

        var settingsPath = Path.Combine(root, ".claude", local ? "settings.local.json" : "settings.json");
        var settings = LoadSettings(settingsPath);
        var hooks = settings["hooks"] as JsonObject ?? new JsonObject();
        settings["hooks"] = hooks;

        var added = 0;
        foreach (var evt in Events)
        {
            var groups = hooks[evt] as JsonArray ?? new JsonArray();
            hooks[evt] = groups;
            if (groups.OfType<JsonObject>().Any(g => (g["hooks"] as JsonArray ?? []).Any(h => IsOurs(h) && !IsGuard(h))))
            {
                continue; // idempotent
            }
            var group = new JsonObject();
            if (ToolEvents.Contains(evt))
            {
                group["matcher"] = "*";
            }
            group["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"{prefix} hook {evt}",
                ["timeout"] = HookTimeoutSeconds,
            });
            groups.Add((JsonNode)group);
            added++;
        }

        if (guard)
        {
            var pre = (JsonArray)hooks["PreToolUse"]!;
            if (!pre.OfType<JsonObject>().Any(g => (g["hooks"] as JsonArray ?? []).Any(IsGuard)))
            {
                pre.Add((JsonNode)new JsonObject
                {
                    ["matcher"] = GuardMatcher,
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"{prefix} guard",
                        ["timeout"] = HookTimeoutSeconds,
                    }),
                });
                added++;
                log.Add("Guard on: force pushes, deleted migrations and edits to recordings are blocked. Add rules under \"guard\" in .blackbox/config.json.");
            }
        }

        if (added > 0)
        {
            Save(settingsPath, settings, log);
            log.Add($"Added {added} hook(s) to {Rel(root, settingsPath)}.");
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
        log.Add("Recording starts with the next Claude Code session in this project.");
        return log;
    }

    public static List<string> Uninstall(string root)
    {
        var log = new List<string>();
        foreach (var name in new[] { "settings.json", "settings.local.json" })
        {
            var path = Path.Combine(root, ".claude", name);
            if (!File.Exists(path))
            {
                continue;
            }
            var settings = LoadSettings(path);
            if (settings["hooks"] is not JsonObject hooks)
            {
                continue;
            }
            var removed = 0;
            foreach (var evt in hooks.Select(p => p.Key).ToList())
            {
                if (hooks[evt] is not JsonArray groups)
                {
                    continue;
                }
                foreach (var group in groups.OfType<JsonObject>().ToList())
                {
                    if (group["hooks"] is not JsonArray list)
                    {
                        continue;
                    }
                    foreach (var h in list.Where(IsOurs).ToList())
                    {
                        list.Remove(h);
                        removed++;
                    }
                    if (list.Count == 0)
                    {
                        groups.Remove(group);
                    }
                }
                if (groups.Count == 0)
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
                log.Add($"Removed {removed} bbx hook(s) from .claude/{name}.");
            }
        }
        if (log.Count == 0)
        {
            log.Add("No bbx hooks found. Nothing changed.");
        }
        log.Add(".blackbox/ was left in place.");
        return log;
    }

    public static bool IsOurs(JsonNode? hook) =>
        hook?["command"] is JsonValue v && v.TryGetValue<string>(out var cmd) && OurCommand().IsMatch(cmd);

    public static bool IsGuard(JsonNode? hook) =>
        hook?["command"] is JsonValue v && v.TryGetValue<string>(out var cmd) && GuardCommand().IsMatch(cmd);

    [GeneratedRegex(@"bbx(?:\.exe)?""?\s+(?:hook\s+\w+|guard)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OurCommand();

    [GeneratedRegex(@"bbx(?:\.exe)?""?\s+guard\s*$", RegexOptions.IgnoreCase)]
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
