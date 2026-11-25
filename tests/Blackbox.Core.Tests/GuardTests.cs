using System.Text.Json.Nodes;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class GuardTests
{
    private static readonly IReadOnlyList<GuardRule> Defaults = Guard.Rules(new BlackboxConfig());

    private static JsonObject Bash(string command, string? cwd = null, string tool = "Bash") => new()
    {
        ["session_id"] = "s1",
        ["hook_event_name"] = "PreToolUse",
        ["cwd"] = cwd,
        ["tool_name"] = tool,
        ["tool_input"] = new JsonObject { ["command"] = command },
    };

    private static JsonObject Edit(string path, string tool = "Edit") => new()
    {
        ["session_id"] = "s1",
        ["hook_event_name"] = "PreToolUse",
        ["tool_name"] = tool,
        ["tool_input"] = new JsonObject { [tool == "NotebookEdit" ? "notebook_path" : "file_path"] = path },
    };

    private static string? Blocked(JsonObject input, string root = "/repo", IReadOnlyList<GuardRule>? rules = null) =>
        Guard.Evaluate(input, root, rules ?? Defaults)?.Rule.Id;

    [Theory]
    [InlineData("git push --force")]
    [InlineData("git push -f origin main")]
    [InlineData("git push -uf origin main")]
    [InlineData("git -C src push origin +main")]
    [InlineData("cd x && git push --force origin feat")]
    public void ForcePush_IsBlocked(string command) => Assert.Equal("force-push", Blocked(Bash(command)));

    [Theory]
    [InlineData("git push")]
    [InlineData("git push -u origin feat/x")]
    [InlineData("git push --force-with-lease origin feat")]
    [InlineData("git push --follow-tags")]
    [InlineData("git commit -m 'git push --force is bad'; git status")]
    public void OrdinaryPush_IsAllowed(string command) => Assert.Null(Blocked(Bash(command)));

    [Theory]
    [InlineData("dotnet ef database drop --force")]
    [InlineData("dotnet-ef database drop")]
    public void DropDatabase_IsBlocked(string command) => Assert.Equal("drop-database", Blocked(Bash(command)));

    [Theory]
    [InlineData("rm src/Data/Migrations/20240101_Init.cs")]
    [InlineData("rm -rf Migrations")]
    [InlineData("rm -rf src/Data/Migrations/")]
    [InlineData("git rm src/Data/Migrations/20240101_Init.Designer.cs")]
    [InlineData("dotnet build && rm 'src/Data/Migrations/x y.cs'")]
    [InlineData("rm /repo/src/Data/Migrations/x.cs")]
    public void DeletingMigrations_IsBlocked(string command) => Assert.Equal("delete-migrations", Blocked(Bash(command, "/repo")));

    [Fact]
    public void DeletingMigrations_InPowerShell_IsBlocked() =>
        Assert.Equal("delete-migrations", Blocked(Bash(@"Remove-Item -Recurse -Force .\src\Data\Migrations\x.cs", "/repo", "PowerShell")));

    [Fact]
    public void DeletingMigrations_RelativeToCwd_IsBlocked() =>
        Assert.Equal("delete-migrations", Blocked(Bash("rm Migrations/x.cs", "/repo/src/Data")));

    [Theory]
    [InlineData("rm src/Orders/OrderService.cs")]
    [InlineData("cat src/Data/Migrations/x.cs")]
    [InlineData("echo rm Migrations/x.cs")]
    [InlineData("dotnet ef migrations add AddOrders")]
    [InlineData("git commit -m \"fix; rm Migrations/x.cs\"")]
    public void OtherCommands_AreAllowed(string command) => Assert.Null(Blocked(Bash(command, "/repo")));

    [Fact]
    public void EditingRecordings_IsBlocked()
    {
        Assert.Equal("protect-recordings", Blocked(Edit("/repo/.blackbox/sessions/2026-01-01_s1.jsonl")));
        Assert.Equal("protect-recordings", Blocked(Edit("/repo/.blackbox/sessions/x.jsonl", "Write")));
        Assert.Equal("protect-recordings", Blocked(Bash("rm -rf .blackbox/sessions", "/repo")));
    }

    [Fact]
    public void EditingOrdinaryFiles_IsAllowed()
    {
        Assert.Null(Blocked(Edit("/repo/src/Orders/OrderService.cs")));
        Assert.Null(Blocked(Edit("/repo/src/Data/Migrations/20240101_Init.cs", "Write")));
        Assert.Null(Blocked(Edit("/repo/.blackbox/config.json")));
        Assert.Null(Blocked(new JsonObject { ["tool_name"] = "Read", ["tool_input"] = new JsonObject { ["file_path"] = "/repo/.blackbox/sessions/x.jsonl" } }));
    }

    [Fact]
    public void Block_SaysWhatMatched()
    {
        var block = Guard.Evaluate(Bash("rm -rf Migrations", "/repo"), "/repo", Defaults)!;
        Assert.Equal("Migrations", block.Target);
        Assert.Contains("migration", block.Rule.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Config_CanDisableDefaults_AndAddRules()
    {
        using var p = new TempProject();
        File.WriteAllText(p.Path("config.json"), """
            {
              "guard": {
                "disable": ["force-push"],
                "rules": [
                  { "id": "no-env", "edit": [".env*", "**/secrets/**"], "reason": "Secrets stay with humans." },
                  { "id": "no-hard-reset", "command": "git\\s+reset\\s+--hard" },
                  { "id": "keep-sql", "delete": "db/*.sql" }
                ]
              }
            }
            """);
        var rules = Guard.Rules(BlackboxConfig.Load(p.Path("config.json")));

        Assert.Null(Blocked(Bash("git push --force"), rules: rules));
        Assert.Equal("delete-migrations", Blocked(Bash("rm Migrations/x.cs", "/repo"), rules: rules));
        Assert.Equal("no-env", Blocked(Edit("/repo/.env.local", "Write"), rules: rules));
        Assert.Equal("no-hard-reset", Blocked(Bash("git reset --hard HEAD~1"), rules: rules));
        Assert.Equal("keep-sql", Blocked(Bash("rm db/schema.sql", "/repo"), rules: rules));
        Assert.Equal("Blocked by guard rule 'no-hard-reset'.", rules.Single(r => r.Id == "no-hard-reset").Reason);
    }

    [Theory]
    [InlineData("""{ "guard": { "rules": [ { "id": "x" } ] } }""")]
    [InlineData("""{ "guard": { "rules": [ { "command": "rm" } ] } }""")]
    [InlineData("""{ "guard": { "rules": [ { "id": "x", "command": "(" } ] } }""")]
    public void BadRules_Throw(string json)
    {
        using var p = new TempProject();
        File.WriteAllText(p.Path("config.json"), json);
        var config = BlackboxConfig.Load(p.Path("config.json")); // the recorder must still load it
        Assert.Throws<InvalidDataException>(() => Guard.Rules(config));
    }

    [Fact]
    public void Check_WithoutBlackbox_AllowsEverything()
    {
        using var p = new TempProject();
        Assert.Null(Guard.Check(Bash("git push --force", p.Root).ToJsonString(), p.Root));
    }

    [Fact]
    public void Check_BlocksAndRecordsTheBlock()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".blackbox", "sessions"));
        var input = Bash("git push --force", p.Root);
        input["tool_use_id"] = "toolu_1";

        var outcome = Guard.Check(input.ToJsonString(), p.Root)!;
        Assert.Equal("force-push", outcome.Block.Rule.Id);
        Guard.Record(outcome, input.ToJsonString(), "test", DateTimeOffset.UnixEpoch);

        var record = JsonNode.Parse(File.ReadAllLines(Directory.GetFiles(p.Path(".blackbox", "sessions")).Single()).Single())!.AsObject();
        Assert.Equal("GuardBlocked", record.Event());
        Assert.Equal("ToolBlocked", record.Kind());
        Assert.Equal("force-push", record.O("data").S("rule"));
        Assert.Equal("git push --force", record.O("data").O("input").S("command"));
        Assert.StartsWith("Blocked by guard (force-push): Bash `git push --force`", SessionAnalysis.Describe(record));
    }
}
