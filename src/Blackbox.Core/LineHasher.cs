using System.Text.RegularExpressions;

namespace Blackbox.Core;

/// <summary>Normalized line hashes used for provenance (bbx blame).</summary>
public static partial class LineHasher
{
    /// <returns>"h:" + 16 hex chars, or null for trivial lines (blank, braces, punctuation only).</returns>
    public static string? Hash(string line)
    {
        var normalized = Normalize(line);
        if (normalized.Length < 2 || !normalized.Any(char.IsLetterOrDigit))
        {
            return null;
        }
        return "h:" + HashChain.Sha256Hex(normalized)[..16];
    }

    public static string Normalize(string line) => Whitespace().Replace(line, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
