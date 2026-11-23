using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Blackbox.Core;

/// <summary>Pattern-based secret redaction. Runs on every string in a record before it is written to disk.</summary>
public sealed partial class Redactor
{
    private readonly (Regex Pattern, string Kind)[] _rules;
    private readonly (string Find, string Replace)[] _literals;

    /// <param name="literals">Exact strings to replace first, e.g. the user's home directory (see <see cref="PersonalLiterals"/>).</param>
    public Redactor(IEnumerable<string>? extraPatterns = null, IEnumerable<(string Find, string Replace)>? literals = null)
    {
        // Longest first, so "C:\Users\me\AppData" is replaced before "me".
        _literals = [.. (literals ?? []).Where(l => l.Find.Length > 0).OrderByDescending(l => l.Find.Length)];
        var rules = new List<(Regex, string)>
        {
            (PrivateKey(), "private_key"),
            (AwsKey(), "aws_key"),
            (GitHubToken(), "github_token"),
            (ApiKey(), "api_key"),
            (Jwt(), "jwt"),
            (UrlCredentials(), "url_credentials"),
            (EnvSecret(), "env_secret"),
            (SecretAssignment(), "password"),
            (WindowsHome(), "home"),
            (UnixHome(), "home"),
        };
        foreach (var p in extraPatterns ?? [])
        {
            rules.Add((new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)), "custom"));
        }
        _rules = [.. rules];
    }

    public string Redact(string text)
    {
        foreach (var (find, replace) in _literals)
        {
            text = text.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (pattern, kind) in _rules)
        {
            text = pattern.Replace(text, m =>
            {
                if (kind == "home")
                {
                    return "~";
                }
                // Rules with a "secret" group keep the key name (e.g. "Password=") and replace only the value.
                var secret = m.Groups["secret"];
                if (!secret.Success)
                {
                    return $"[REDACTED:{kind}]";
                }
                var start = secret.Index - m.Index;
                return m.Value[..start] + $"[REDACTED:{kind}]" + m.Value[(start + secret.Length)..];
            });
        }
        return text;
    }

    /// <summary>Redacts every string value in the tree, in place.</summary>
    public void RedactTree(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsString(obj[key], out var s))
                    {
                        obj[key] = Redact(s);
                    }
                    else
                    {
                        RedactTree(obj[key]);
                    }
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (IsString(arr[i], out var s))
                    {
                        arr[i] = Redact(s);
                    }
                    else
                    {
                        RedactTree(arr[i]);
                    }
                }
                break;
        }
    }

    private static bool IsString(JsonNode? n, out string value)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            value = s;
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>The current user's home directory (in the spellings shells use) and user name, so logs committed to git don't carry them.</summary>
    public static List<(string Find, string Replace)> PersonalLiterals()
    {
        var list = new List<(string, string)>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        if (home.Length > 3)
        {
            list.Add((home, "~"));
            list.Add((home.Replace('\\', '/'), "~"));
            if (home.Length > 2 && home[1] == ':')
            {
                list.Add(("/" + char.ToLowerInvariant(home[0]) + home[2..].Replace('\\', '/'), "~")); // Git Bash: /c/Users/me
            }
        }
        var user = Environment.UserName;
        if (user.Length >= 4)
        {
            list.Add((user, "<user>"));
        }
        return list;
    }

    // Other users' homes and 8.3 short names (C:\Users\JOHNDO~1) that the literal pass can't know about.
    [GeneratedRegex(@"(?i)\b[A-Z]:[\\/]Users[\\/][^\\/\s""'<>|]+")]
    private static partial Regex WindowsHome();

    [GeneratedRegex(@"(?<![\w.~])/(?:home|Users)/[^/\s""'<>|]+")]
    private static partial Regex UnixHome();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?<=[a-zA-Z][a-zA-Z0-9+.\-]*://[^/\s:@]+:)(?<secret>[^@\s/]+)(?=@)")]
    private static partial Regex UrlCredentials();

    // .env style: UPPER_CASE name containing KEY/SECRET/TOKEN/PASSWORD at line start.
    [GeneratedRegex(@"(?m)^[ \t]*(?:export[ \t]+)?[A-Z0-9_]*(?:KEY|SECRET|TOKEN|PASSWORD|PASSWD|PWD|CREDENTIALS?)[A-Z0-9_]*[ \t]*=[ \t]*(?<secret>[^\r\n]+?)[ \t]*$")]
    private static partial Regex EnvSecret();

    // password=..., "apiKey": "...", Password=...; in connection strings.
    [GeneratedRegex(@"(?i)\b(?:password|passwd|pwd|secret|api[_\-]?key|access[_\-]?token|auth[_\-]?token|client[_\-]?secret)[""']?[ \t]*[:=][ \t]*(?<secret>""[^""\r\n]*""|'[^'\r\n]*'|[^\s;,&'""]+)")]
    private static partial Regex SecretAssignment();
}
