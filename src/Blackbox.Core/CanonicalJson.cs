using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blackbox.Core;

/// <summary>Deterministic JSON: object keys sorted ordinally, no whitespace, UTF-8, numbers written as their JSON text.</summary>
public static class CanonicalJson
{
    // Relaxed escaping keeps non-ASCII text (e.g. Turkish prompts) readable in the log; output stays deterministic.
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    public static string Serialize(JsonNode? node) => Encoding.UTF8.GetString(SerializeToUtf8(node));

    public static byte[] SerializeToUtf8(JsonNode? node)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, Options))
        {
            Write(writer, node);
        }
        return ms.ToArray();
    }

    private static void Write(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                break;
            case JsonObject obj:
                w.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    Write(w, value);
                }
                w.WriteEndObject();
                break;
            case JsonArray arr:
                w.WriteStartArray();
                foreach (var item in arr)
                {
                    Write(w, item);
                }
                w.WriteEndArray();
                break;
            default:
                node.WriteTo(w);
                break;
        }
    }
}
