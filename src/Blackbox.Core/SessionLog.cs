using System.Globalization;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

/// <summary>Small typed accessors for record JSON.</summary>
public static class J
{
    public static string? S(this JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static int? I(this JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static bool? B(this JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static JsonObject? O(this JsonNode? node, string key) => node?[key] as JsonObject;

    public static DateTimeOffset Ts(this JsonObject record) =>
        DateTimeOffset.TryParse(record.S("ts"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MinValue;

    public static string? Kind(this JsonObject r) => r.S("kind");

    public static string? Event(this JsonObject r) => r.S("event");

    public static int Seq(this JsonObject r) => r.I("seq") ?? -1;

    public static bool IsToolResult(this JsonObject r) => r.Event() is "PostToolUse" or "PostToolUseFailure";

    public static bool IsFileChange(this JsonObject r) => r.Kind() is "FileCreated" or "FileModified" && r.O("data").S("op") is not null;

    /// <summary>Background task notifications arrive as UserPromptSubmit. Logs before 0.1.2 stored them as PromptSubmitted.</summary>
    public static bool IsNotification(this JsonObject r) =>
        r.Kind() == "TaskNotification"
        || (r.Kind() == "PromptSubmitted" && RecordBuilder.IsNotificationText(r.O("data").S("prompt")));

    /// <summary>A prompt the user typed (not a task notification).</summary>
    public static bool IsPrompt(this JsonObject r) => r.Kind() == "PromptSubmitted" && !r.IsNotification();

    /// <summary>A SessionStart after the first record: resume, compact or clear within the same session.</summary>
    public static bool IsRestart(this JsonObject r) => r.Kind() == "SessionStarted" && r.Seq() > 0;
}

/// <summary>One recorded session, loaded from its JSONL file.</summary>
public sealed class SessionLog
{
    public required string File { get; init; }
    public required string Id { get; init; }
    public required IReadOnlyList<JsonObject> Records { get; init; }
    public required VerifyResult Verify { get; init; }

    public DateTimeOffset Start => Records.Count > 0 ? Records[0].Ts() : DateTimeOffset.MinValue;
    public DateTimeOffset End => Records.Count > 0 ? Records[^1].Ts() : DateTimeOffset.MinValue;
    public string ShortId => Id.Length > 8 ? Id[..8] : Id;

    public JsonObject? StartRecord => Records.FirstOrDefault(r => r.Kind() == "SessionStarted");
    public string? Branch => StartRecord.O("data").S("git_branch");
    public string? Head => StartRecord.O("data").S("git_head");

    public IEnumerable<JsonObject> Prompts => Records.Where(r => r.IsPrompt());
    public IEnumerable<JsonObject> ToolResults => Records.Where(r => r.IsToolResult());

    // Attribution is derived from record order rather than the stored prompt_seq, so older logs
    // (where notifications moved prompt_seq) read correctly without rewriting the chain.
    private Dictionary<JsonObject, JsonObject>? _promptOf;
    private Dictionary<JsonObject, int>? _promptNumber;

    private void Index()
    {
        if (_promptOf is not null)
        {
            return;
        }
        _promptOf = new(ReferenceEqualityComparer.Instance);
        _promptNumber = new(ReferenceEqualityComparer.Instance);
        JsonObject? current = null;
        foreach (var r in Records)
        {
            if (r.IsPrompt())
            {
                current = r;
                _promptNumber[r] = _promptNumber.Count + 1;
            }
            else if (r.IsRestart())
            {
                current = null;
            }
            if (current is not null)
            {
                _promptOf[r] = current;
            }
        }
    }

    /// <summary>The user prompt this record belongs to, or null (before the first prompt, or after a resume).</summary>
    public JsonObject? PromptOf(JsonObject record)
    {
        Index();
        return _promptOf!.GetValueOrDefault(record);
    }

    /// <summary>1-based position of a prompt among the session's user prompts.</summary>
    public int PromptNumber(JsonObject prompt)
    {
        Index();
        return _promptNumber!.GetValueOrDefault(prompt);
    }

    public JsonObject? PromptAt(int? seq) =>
        seq is null ? null : Records.FirstOrDefault(r => r.Seq() == seq && r.IsPrompt());

    /// <summary>Loads a session. Records after a broken link are still returned; check <see cref="Verify"/>.</summary>
    public static SessionLog Load(string file)
    {
        var records = new List<JsonObject>();
        foreach (var line in System.IO.File.ReadLines(file))
        {
            if (line.Length == 0)
            {
                continue;
            }
            try
            {
                if (JsonNode.Parse(line) is JsonObject o)
                {
                    records.Add(o);
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Unreadable line: verify reports it; readers skip it.
            }
        }
        var verify = ChainVerifier.VerifyFile(file);
        var id = records.Select(r => r.S("session")).FirstOrDefault(s => s is not null)
            ?? Path.GetFileNameWithoutExtension(file);
        return new SessionLog { File = file, Id = id, Records = records, Verify = verify };
    }

    public static List<SessionLog> LoadAll(string root) =>
        [.. new SessionStore(Path.Combine(root, HookRecorder.DirName)).AllSessionFiles().Select(Load).OrderBy(s => s.Start)];

    /// <summary>Finds a session by id prefix, or the most recent one for "latest" / null.</summary>
    public static SessionLog Find(IReadOnlyList<SessionLog> sessions, string? idOrLatest)
    {
        if (sessions.Count == 0)
        {
            throw new InvalidOperationException("No recorded sessions yet.");
        }
        if (string.IsNullOrEmpty(idOrLatest) || idOrLatest == "latest")
        {
            return sessions.MaxBy(s => s.End)!;
        }
        var matches = sessions.Where(s => s.Id.StartsWith(idOrLatest, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"No session matches '{idOrLatest}'. Run 'bbx sessions' to list them."),
            _ => throw new InvalidOperationException($"'{idOrLatest}' matches {matches.Count} sessions: {string.Join(", ", matches.Select(m => m.ShortId))}. Use a longer prefix."),
        };
    }
}
