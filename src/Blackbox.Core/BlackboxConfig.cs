using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blackbox.Core;

public sealed class BlackboxConfig
{
    public static readonly string[] DefaultIgnorePaths = [".env*", "**/secrets/**", "*.pfx", "*.pem", "appsettings.*.json"];

    public string RecordPrompts { get; init; } = "full";
    public int MaxDiffKb { get; init; } = 16;
    public IReadOnlyList<string> IgnorePaths { get; init; } = DefaultIgnorePaths;
    public bool RedactionEnabled { get; init; } = true;
    public IReadOnlyList<string> ExtraPatterns { get; init; } = [];
    public IReadOnlyList<string> TestCommands { get; init; } = [];
    public IReadOnlyList<string> BuildCommands { get; init; } = [];

    /// <summary>Changes to these paths don't make verification stale (docs). Config: "freshness": { "exclude": [...] }.</summary>
    public IReadOnlyList<string> NonSourcePaths { get; init; } = DefaultNonSourcePaths;

    /// <summary>The raw "guard" section. Parsed by Guard.Rules, so a bad guard rule never stops the recorder.</summary>
    public JsonNode? Guard { get; init; }

    public static readonly string[] DefaultNonSourcePaths = ["*.md", "docs/**"];

    private Regex[]? _ignore;
    private Regex[]? _nonSource;

    public bool IsIgnored(string relativePath)
    {
        _ignore ??= [.. IgnorePaths.Select(Glob.ToRegex)];
        return _ignore.Any(r => r.IsMatch(relativePath));
    }

    public bool IsSource(string? relativePath)
    {
        _nonSource ??= [.. NonSourcePaths.Select(Glob.ToRegex)];
        return relativePath is null || !_nonSource.Any(r => r.IsMatch(relativePath));
    }

    public const string DefaultJson = """
        {
          "version": 1,
          "record_prompts": "full",
          "max_diff_kb": 16,
          "ignore_paths": [".env*", "**/secrets/**", "*.pfx", "*.pem", "appsettings.*.json"],
          "redaction": { "enabled": true, "extra_patterns": [] },
          "commands": { "test": [], "build": [] },
          "freshness": { "exclude": ["*.md", "docs/**"] },
          "guard": { "disable": [], "rules": [] }
        }

        """;

    /// <summary>Missing file means defaults. An unreadable file throws: better to record nothing than to ignore the user's privacy settings.</summary>
    public static BlackboxConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new BlackboxConfig();
        }
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: options) as JsonObject
            ?? throw new InvalidDataException("config.json must be a JSON object");

        var mode = root["record_prompts"]?.GetValue<string>() ?? "full";
        if (mode is not ("full" or "hash" or "off"))
        {
            throw new InvalidDataException($"config.json: record_prompts must be full, hash or off (got '{mode}')");
        }
        return new BlackboxConfig
        {
            RecordPrompts = mode,
            MaxDiffKb = root["max_diff_kb"]?.GetValue<int>() ?? 16,
            IgnorePaths = Strings(root["ignore_paths"]) ?? DefaultIgnorePaths,
            RedactionEnabled = root["redaction"]?["enabled"]?.GetValue<bool>() ?? true,
            ExtraPatterns = Strings(root["redaction"]?["extra_patterns"]) ?? [],
            TestCommands = Strings(root["commands"]?["test"]) ?? [],
            BuildCommands = Strings(root["commands"]?["build"]) ?? [],
            NonSourcePaths = Strings(root["freshness"]?["exclude"]) ?? DefaultNonSourcePaths,
            Guard = root["guard"],
        };
    }

    private static string[]? Strings(JsonNode? n) =>
        n is JsonArray a ? [.. a.Select(x => x!.GetValue<string>())] : null;
}
