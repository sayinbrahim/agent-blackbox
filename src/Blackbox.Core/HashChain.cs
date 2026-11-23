using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

public static class HashChain
{
    public const string Genesis = "sha256:GENESIS";

    /// <summary>hash = SHA256(prev + "\n" + canonical_json(record without "hash")).</summary>
    public static string Compute(JsonObject record)
    {
        var prev = record["prev"]?.GetValue<string>() ?? Genesis;
        var copy = (JsonObject)record.DeepClone();
        copy.Remove("hash");
        return "sha256:" + Sha256Hex(prev + "\n" + CanonicalJson.Serialize(copy));
    }

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed record VerifyResult(string File, string? Session, int Records, string? Error)
{
    public bool Ok => Error is null;
}

public static class ChainVerifier
{
    public static VerifyResult VerifyFile(string path)
    {
        string? session = null;
        var expectedPrev = HashChain.Genesis;
        var expectedSeq = 0;
        var lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject record;
            try
            {
                record = JsonNode.Parse(line) as JsonObject ?? throw new FormatException("not an object");
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException)
            {
                return new(path, session, expectedSeq, $"invalid record at line {lineNo} ({ex.Message})");
            }

            var seq = TryInt(record["seq"]);
            session ??= TryString(record["session"]);
            if (seq != expectedSeq)
            {
                return new(path, session, expectedSeq, seq is null
                    ? $"tampered at line {lineNo} (missing seq)"
                    : $"tampered at seq {seq} (expected seq {expectedSeq}: missing or reordered record)");
            }
            if (TryString(record["session"]) != session)
            {
                return new(path, session, expectedSeq, $"tampered at seq {seq} (session id mismatch)");
            }
            if (TryString(record["prev"]) != expectedPrev)
            {
                return new(path, session, expectedSeq, $"tampered at seq {seq} (prev hash mismatch)");
            }
            var hash = TryString(record["hash"]);
            if (hash != HashChain.Compute(record))
            {
                return new(path, session, expectedSeq, $"tampered at seq {seq} (hash mismatch)");
            }

            expectedPrev = hash!;
            expectedSeq++;
        }
        return new(path, session, expectedSeq, null);
    }

    private static string? TryString(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? TryInt(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
