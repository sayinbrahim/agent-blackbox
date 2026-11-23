using System.Text.Json.Nodes;
using Blackbox.Core;

namespace Blackbox.Tests.Shared;

/// <summary>Writes a synthetic session through the real store, so readers see properly chained records.</summary>
public sealed class SessionBuilder(TempProject project, string session = "sess-0001")
{
    private DateTimeOffset _now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    private readonly SessionStore _store = new(project.Path(".blackbox"));

    public SessionBuilder At(int minute, int second = 0)
    {
        _now = new DateTimeOffset(2026, 11, 24, 10, minute, second, TimeSpan.Zero);
        return this;
    }

    public SessionBuilder Add(string evt, string? kind, string? tool, JsonObject data, string actor = "agent")
    {
        var r = new JsonObject { ["v"] = 1, ["ts"] = _now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["session"] = session, ["event"] = evt, ["actor"] = actor, ["data"] = data, ["raw_extra"] = new JsonObject() };
        if (kind is not null)
        {
            r["kind"] = kind;
        }
        if (tool is not null)
        {
            r["tool"] = tool;
        }
        _store.Append(r, _now);
        _now = _now.AddSeconds(1);
        return this;
    }

    public SessionBuilder Start(string branch = "main") =>
        Add("SessionStart", "SessionStarted", null, new JsonObject { ["source"] = "startup", ["git_branch"] = branch, ["git_head"] = "0123456789abcdef" }, "system");

    public SessionBuilder Prompt(string text) => Add("UserPromptSubmit", "PromptSubmitted", null, new JsonObject { ["prompt"] = text }, "human");

    /// <summary>Records an agent edit of <paramref name="file"/> that added <paramref name="lines"/>, and writes the file so after_hash matches.</summary>
    public SessionBuilder Edit(string file, params string[] lines)
    {
        var path = project.Path(file.Split('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = string.Join("\n", lines) + "\n";
        File.WriteAllText(path, content);
        var data = new JsonObject
        {
            ["file"] = file, ["op"] = "edit", ["success"] = true, ["added_lines"] = lines.Length, ["removed_lines"] = 0,
            ["added_line_hashes"] = new JsonArray([.. lines.Select(LineHasher.Hash).OfType<string>().Select(h => (JsonNode)h)]),
            ["after_hash"] = "sha256:" + HashChain.Sha256Hex(File.ReadAllBytes(path)),
            ["input"] = new JsonObject { ["file"] = file },
        };
        return Add("PostToolUse", "FileModified", "Edit", data);
    }

    public SessionBuilder Command(string command, bool success = true, string kind = "CommandExecuted", string output = "")
    {
        var data = new JsonObject { ["success"] = success, ["exit_code"] = success ? 0 : 1, ["input"] = new JsonObject { ["command"] = command } };
        data[success ? "output_tail" : "error"] = output;
        return Add(success ? "PostToolUse" : "PostToolUseFailure", kind, "Bash", data);
    }

    public SessionBuilder Test(bool pass = true, string? output = null) =>
        Command("dotnet test", pass, "TestCompleted",
            output ?? (pass ? "Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5" : "Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5"));

    public SessionBuilder Stop() => Add("Stop", "AgentStopped", null, new JsonObject());
}
