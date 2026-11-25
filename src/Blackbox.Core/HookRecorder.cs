using System.Text;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

public static class HookRecorder
{
    public const string DirName = ".blackbox";

    /// <summary>Finds the nearest directory (walking up) that contains .blackbox/.</summary>
    public static string? FindRoot(string? start)
    {
        for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, DirName)))
            {
                return dir.FullName;
            }
        }
        return null;
    }

    /// <summary>Records one hook payload. Returns the project root it wrote to, or null when the project has no .blackbox/. Throws on errors; the caller must swallow them.</summary>
    public static string? Record(string eventArg, string stdinJson, string? projectDir, string bbxVersion, DateTimeOffset now, string agent = AgentAdapter.ClaudeCode)
    {
        var raw = JsonNode.Parse(stdinJson) as JsonObject ?? throw new InvalidDataException("hook input is not a JSON object");
        var inputs = AgentAdapter.Normalize(agent, raw, eventArg);
        var root = FindRoot(projectDir) ?? inputs.Select(i => FindRoot(i["cwd"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : null)).FirstOrDefault(r => r is not null);
        if (root is null)
        {
            return null;
        }
        try
        {
            var blackbox = Path.Combine(root, DirName);
            var config = BlackboxConfig.Load(Path.Combine(blackbox, "config.json"));
            var builder = new RecordBuilder(root, config, bbxVersion);
            var store = new SessionStore(blackbox);
            foreach (var input in inputs)
            {
                store.Append(builder.Build(input, eventArg, now), now);
            }
            return root;
        }
        catch (Exception ex)
        {
            throw new RecorderException(root, ex);
        }
    }

    public static void LogError(string root, Exception ex, DateTimeOffset now)
    {
        // shortcut: errors.log grows without rotation; add a size cap if it ever becomes noisy.
        var line = $"{now.UtcDateTime:O} {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}";
        File.AppendAllText(Path.Combine(root, DirName, "errors.log"), line, Encoding.UTF8);
    }
}

public sealed class RecorderException(string root, Exception inner) : Exception(inner.Message, inner)
{
    public string Root { get; } = root;
}
