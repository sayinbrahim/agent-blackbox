using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

/// <summary>Append-only JSONL session logs under .blackbox/sessions, guarded by .blackbox/.lock.</summary>
public sealed class SessionStore(string blackboxDir)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public string SessionsDir { get; } = Path.Combine(blackboxDir, "sessions");

    /// <summary>Fills seq, prev, prompt_seq and hash inside the lock, then appends the record as one line.</summary>
    public JsonObject Append(JsonObject record, DateTimeOffset now, TimeSpan? lockTimeout = null)
    {
        Directory.CreateDirectory(SessionsDir);
        using var _ = AcquireLock(Path.Combine(blackboxDir, ".lock"), lockTimeout ?? TimeSpan.FromSeconds(1.5));

        var session = SafeId(record["session"]?.GetValue<string>());
        record["session"] = session;
        var path = FindSessionFile(session) ?? Path.Combine(SessionsDir, $"{now.UtcDateTime:yyyy-MM-ddTHH-mm-ssZ}_{session}.jsonl");

        var lastLine = ReadLastLine(path);
        var last = lastLine is null ? null : JsonNode.Parse(lastLine) as JsonObject;
        var seq = last is null ? 0 : last["seq"]!.GetValue<int>() + 1;
        record["seq"] = seq;
        record["prev"] = last?["hash"]?.GetValue<string>() ?? HashChain.Genesis;

        // prompt_seq links every action to the latest user prompt; a prompt points to itself.
        // Task notifications keep the current prompt; a SessionStart (resume, compact) belongs to none.
        var evt = record["event"]?.GetValue<string>();
        var promptSeq = evt switch
        {
            "UserPromptSubmit" when record["kind"]?.GetValue<string>() != "TaskNotification" => seq,
            "SessionStart" => (int?)null,
            _ => last?["prompt_seq"]?.GetValue<int>(),
        };
        if (promptSeq is not null)
        {
            record["prompt_seq"] = promptSeq;
        }
        record.Remove("hash");
        record["hash"] = HashChain.Compute(record);

        File.AppendAllText(path, CanonicalJson.Serialize(record) + "\n", Utf8NoBom);
        return record;
    }

    public string? FindSessionFile(string session) =>
        Directory.Exists(SessionsDir) ? Directory.GetFiles(SessionsDir, $"*_{SafeId(session)}.jsonl").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null;

    public IEnumerable<string> AllSessionFiles() =>
        Directory.Exists(SessionsDir) ? Directory.GetFiles(SessionsDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal) : [];

    public static string SafeId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return "unknown";
        }
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars, 0, Math.Min(chars.Length, 128));
    }

    private static FileStream AcquireLock(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var delay = 5;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (sw.Elapsed < timeout)
            {
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 50);
            }
        }
    }

    /// <summary>Reads the last non-empty line by scanning backwards, so appends stay O(1) in log size.</summary>
    internal static string? ReadLastLine(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = Array.Empty<byte>();
        var pos = fs.Length;
        while (pos > 0)
        {
            var n = (int)Math.Min(8192, pos);
            pos -= n;
            var chunk = new byte[n];
            fs.Position = pos;
            fs.ReadExactly(chunk);
            buffer = [.. chunk, .. buffer];

            var end = buffer.Length;
            while (end > 0 && buffer[end - 1] is (byte)'\n' or (byte)'\r')
            {
                end--;
            }
            var nl = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(end - 1, 0));
            if (end > 0 && nl >= 0 && nl < end)
            {
                return Encoding.UTF8.GetString(buffer, nl + 1, end - nl - 1);
            }
            if (pos == 0)
            {
                return end == 0 ? null : Encoding.UTF8.GetString(buffer, 0, end);
            }
        }
        return null;
    }
}
