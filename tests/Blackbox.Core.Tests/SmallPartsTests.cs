using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class GlobTests
{
    [Theory]
    [InlineData(".env*", ".env", true)]
    [InlineData(".env*", "src/.env.local", true)]
    [InlineData(".env*", "src/my.env", false)]
    [InlineData("**/secrets/**", "secrets/a.txt", true)]
    [InlineData("**/secrets/**", "src/secrets/deep/a.txt", true)]
    [InlineData("**/secrets/**", "src/secretsx/a.txt", false)]
    [InlineData("*.pem", "certs/server.pem", true)]
    [InlineData("appsettings.*.json", "src/Api/appsettings.Production.json", true)]
    [InlineData("appsettings.*.json", "src/Api/appsettings.json", false)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/x/a.cs", false)]
    [InlineData("data/?.csv", "data/a.csv", true)]
    public void Matches(string glob, string path, bool expected) => Assert.Equal(expected, Glob.ToRegex(glob).IsMatch(path));
}

public class ConfigTests
{
    [Fact]
    public void MissingFile_GivesDefaults()
    {
        var c = BlackboxConfig.Load("does-not-exist.json");
        Assert.Equal("full", c.RecordPrompts);
        Assert.Equal(16, c.MaxDiffKb);
        Assert.True(c.IsIgnored(".env"));
    }

    [Fact]
    public void DefaultJson_RoundTrips()
    {
        using var p = new TempProject();
        File.WriteAllText(p.Path("c.json"), BlackboxConfig.DefaultJson);
        var c = BlackboxConfig.Load(p.Path("c.json"));
        Assert.Equal(BlackboxConfig.DefaultIgnorePaths, c.IgnorePaths);
        Assert.True(c.RedactionEnabled);
    }

    [Fact]
    public void ReadsValues_AllowsComments()
    {
        using var p = new TempProject();
        File.WriteAllText(p.Path("c.json"), """
            { // comment
              "record_prompts": "hash", "max_diff_kb": 4, "ignore_paths": ["*.key"],
              "redaction": { "enabled": false, "extra_patterns": ["X"] },
              "commands": { "test": ["make check"] },
            }
            """);
        var c = BlackboxConfig.Load(p.Path("c.json"));
        Assert.Equal("hash", c.RecordPrompts);
        Assert.Equal(4, c.MaxDiffKb);
        Assert.True(c.IsIgnored("a/b.key"));
        Assert.False(c.IsIgnored(".env"));
        Assert.False(c.RedactionEnabled);
        Assert.Equal(["make check"], c.TestCommands);
    }

    [Fact]
    public void InvalidRecordPrompts_Throws()
    {
        using var p = new TempProject();
        File.WriteAllText(p.Path("c.json"), """{"record_prompts":"some"}""");
        Assert.Throws<InvalidDataException>(() => BlackboxConfig.Load(p.Path("c.json")));
    }
}

public class LineHasherTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("  });")]
    [InlineData("*/")]
    [InlineData("x")]
    public void TrivialLines_AreSkipped(string line) => Assert.Null(LineHasher.Hash(line));

    [Fact]
    public void WhitespaceDifferences_HashTheSame()
    {
        Assert.Equal(LineHasher.Hash("var x = 1;"), LineHasher.Hash("\t  var   x =\t1;  \r"));
        Assert.NotEqual(LineHasher.Hash("var x = 1;"), LineHasher.Hash("var x = 2;"));
    }

    [Fact]
    public void Format_IsPrefixedShortHex()
    {
        Assert.Matches("^h:[0-9a-f]{16}$", LineHasher.Hash("return result;")!);
    }
}

public class CommandClassifierTests
{
    private readonly CommandClassifier _c = new(new BlackboxConfig());

    [Theory]
    [InlineData("dotnet test", CommandKind.Test)]
    [InlineData("cd src && dotnet test --no-build", CommandKind.Test)]
    [InlineData("npm test", CommandKind.Test)]
    [InlineData("npm run test -- --watch=false", CommandKind.Test)]
    [InlineData("pnpm test", CommandKind.Test)]
    [InlineData("yarn test", CommandKind.Test)]
    [InlineData("python -m pytest -q", CommandKind.Test)]
    [InlineData("go test ./...", CommandKind.Test)]
    [InlineData("cargo test", CommandKind.Test)]
    [InlineData("mvn test", CommandKind.Test)]
    [InlineData("./gradlew test", CommandKind.Test)]
    [InlineData("dotnet build && dotnet test", CommandKind.Test)]
    [InlineData("dotnet build -c Release", CommandKind.Build)]
    [InlineData("npm run build", CommandKind.Build)]
    [InlineData("go build ./cmd/x", CommandKind.Build)]
    [InlineData("cargo build --release", CommandKind.Build)]
    [InlineData("mvn package", CommandKind.Build)]
    [InlineData("npx tsc --noEmit", CommandKind.Build)]
    [InlineData("rm -rf bin", CommandKind.Delete)]
    [InlineData("git rm old.cs", CommandKind.Delete)]
    [InlineData("Remove-Item foo.txt", CommandKind.Delete)]
    [InlineData("git status", CommandKind.Other)]
    [InlineData("echo latest", CommandKind.Other)]
    [InlineData("dotnet testing-tool", CommandKind.Other)]
    [InlineData("cat format.txt", CommandKind.Other)]
    public void Classifies(string command, CommandKind expected) => Assert.Equal(expected, _c.Classify(command));

    [Fact]
    public void ExtraPatterns_FromConfig()
    {
        var c = new CommandClassifier(new BlackboxConfig { TestCommands = [@"^make check\b"], BuildCommands = [@"^make\b"] });
        Assert.Equal(CommandKind.Test, c.Classify("make check"));
        Assert.Equal(CommandKind.Build, c.Classify("make all"));
    }
}
