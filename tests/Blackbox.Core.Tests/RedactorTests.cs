using System.Text.Json.Nodes;
using Blackbox.Core;

namespace Blackbox.Core.Tests;

public class RedactorTests
{
    private readonly Redactor _r = new();

    [Theory]
    [InlineData("key AKIAIOSFODNN7EXAMPLE here", "key [REDACTED:aws_key] here")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "[REDACTED:github_token]")]
    [InlineData("token github_pat_11ABCDEFG0123456789_abcdefghijklmnop", "token [REDACTED:github_token]")]
    [InlineData("export ANTHROPIC=sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "export ANTHROPIC=[REDACTED:api_key]")]
    [InlineData("sk-proj-ABCDEFGHIJKLMNOPQRSTUVWX", "[REDACTED:api_key]")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "Bearer [REDACTED:jwt]")]
    [InlineData("postgres://admin:hunter2@db.local:5432/app", "postgres://admin:[REDACTED:url_credentials]@db.local:5432/app")]
    [InlineData("Server=db;User Id=sa;Password=P@ssw0rd!;Encrypt=true", "Server=db;User Id=sa;Password=[REDACTED:password];Encrypt=true")]
    [InlineData("password=hunter2", "password=[REDACTED:password]")]
    [InlineData("{\"apiKey\": \"abc123secret\"}", "{\"apiKey\": [REDACTED:password]}")]
    [InlineData("client_secret: s3cr3t", "client_secret: [REDACTED:password]")]
    public void RedactsSecrets(string input, string expected) => Assert.Equal(expected, _r.Redact(input));

    [Fact]
    public void RedactsEnvFileLines()
    {
        var env = "DEBUG=true\nSTRIPE_SECRET_KEY=sk_live_abc\nexport DB_PASSWORD = x y z\nPORT=8080";
        Assert.Equal("DEBUG=true\nSTRIPE_SECRET_KEY=[REDACTED:env_secret]\nexport DB_PASSWORD = [REDACTED:env_secret]\nPORT=8080", _r.Redact(env));
    }

    [Fact]
    public void RedactsPrivateKeyBlock()
    {
        var pem = "before\n-----BEGIN RSA PRIVATE KEY-----\nMIIEow\nabc\n-----END RSA PRIVATE KEY-----\nafter";
        Assert.Equal("before\n[REDACTED:private_key]\nafter", _r.Redact(pem));
    }

    [Fact]
    public void RedactsTruncatedPrivateKeyToEnd()
    {
        Assert.Equal("x [REDACTED:private_key]", _r.Redact("x -----BEGIN PRIVATE KEY-----\nMIIEow...[truncated]"));
    }

    [Theory]
    [InlineData("Add a password reset feature")]
    [InlineData("var tokenizer = new Tokenizer();")]
    [InlineData("sk-short")]
    [InlineData("ghp_tooShort")]
    [InlineData("AKIA1234")]
    [InlineData("https://github.com/sayinbrahim/agent-blackbox")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9 alone")]
    [InlineData("DEBUG=true")]
    [InlineData("dotnet test --filter Category=Unit")]
    public void LeavesNonSecretsAlone(string input) => Assert.Equal(input, _r.Redact(input));

    [Fact]
    public void IsIdempotent()
    {
        var once = _r.Redact("password=hunter2 ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        Assert.Equal(once, _r.Redact(once));
    }

    [Theory]
    [InlineData(@"C:\Users\JOHNDO~1\AppData\Local\Temp\x", @"~\AppData\Local\Temp\x")]
    [InlineData("C:/Users/someone/repo", "~/repo")]
    [InlineData("/home/dev/.claude/x", "~/.claude/x")]
    [InlineData("cd /Users/ana/code && ls", "cd ~/code && ls")]
    public void HomeDirectories_BecomeTilde(string input, string expected) => Assert.Equal(expected, _r.Redact(input));

    [Fact]
    public void Literals_HandleNamesWithSpaces()
    {
        var r = new Redactor(literals: [(@"C:\Users\Ana María", "~"), ("Ana María", "<user>")]);
        Assert.Equal(@"~\repo and -rw-r--r-- 1 <user> 197121", r.Redact(@"C:\Users\Ana María\repo and -rw-r--r-- 1 Ana María 197121"));
    }

    [Theory]
    [InlineData(@"C:\Users\JOHNDO~1\AppData\Local\Temp", "JOHNDO~1")]
    [InlineData(@"C:\Users\JOHNDO~12\AppData", "JOHNDO~12")]
    [InlineData(@"C:\Users\john\AppData\Local\Temp", null)]
    [InlineData(null, null)]
    public void ShortProfileName_FromTemp(string? temp, string? expected) => Assert.Equal(expected, Redactor.ShortProfileName(temp));

    [Fact]
    public void PersonalLiterals_IncludeCurrentHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(Redactor.PersonalLiterals(), l => l.Find == home.TrimEnd('\\', '/'));
    }

    [Fact]
    public void ExtraPatterns_AreApplied()
    {
        var r = new Redactor(["ACME-[0-9]{6}"]);
        Assert.Equal("id [REDACTED:custom]", r.Redact("id ACME-123456"));
    }

    [Fact]
    public void RedactTree_RedactsNestedStrings_LeavesOtherValues()
    {
        var tree = new JsonObject
        {
            ["prompt"] = "use password=hunter2",
            ["n"] = 5,
            ["list"] = new JsonArray("ghp_abcdefghijklmnopqrstuvwxyz0123456789", true),
            ["inner"] = new JsonObject { ["diff"] = "+AWS=AKIAIOSFODNN7EXAMPLE" },
        };
        _r.RedactTree(tree);
        Assert.Equal("use password=[REDACTED:password]", tree["prompt"]!.GetValue<string>());
        Assert.Equal(5, tree["n"]!.GetValue<int>());
        Assert.Equal("[REDACTED:github_token]", tree["list"]![0]!.GetValue<string>());
        Assert.True(tree["list"]![1]!.GetValue<bool>());
        Assert.Equal("+AWS=[REDACTED:aws_key]", tree["inner"]!["diff"]!.GetValue<string>());
    }
}
