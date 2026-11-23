using System.Text.Json.Nodes;
using Blackbox.Cli;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Cli.Tests;

// Tests that swap Console.Out must not run in parallel with each other.
[CollectionDefinition("console", DisableParallelization = true)]
public class ConsoleCollection;

[Collection("console")]
public class HookEndToEndTests
{
    private static readonly string[] Session =
    [
        "SessionStart.json", "UserPromptSubmit.json", "PreToolUse.edit.json", "PostToolUse.edit.json",
        "PostToolUse.read.json", "PostToolUseFailure.bash.json", "PostToolUse.bash-test.json", "Stop.json", "SessionEnd.json",
    ];

    private static (int Exit, string Stdout, string Stderr) Capture(Func<int> run)
    {
        var (o, e) = (Console.Out, Console.Error);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            return (run(), stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(o);
            Console.SetError(e);
        }
    }

    private static int Hook(TempProject p, string fixture) =>
        Program.RunHook("", new StringReader(Fixtures.Load(fixture, p.Root)), p.Root);

    [Fact]
    public void FullSession_IsRecorded_Redacted_AndVerifies()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, local: false, makePrivate: false, processPath: null);
        Directory.CreateDirectory(p.Path("src"));
        File.WriteAllText(p.Path("src", "sample.txt"), "line one\nline two changed\nline three\n");

        foreach (var f in Session)
        {
            var (exit, stdout, stderr) = Capture(() => Hook(p, f));
            Assert.Equal(0, exit);
            Assert.Equal("", stdout);
            Assert.Equal("", stderr);
        }

        var file = Assert.Single(Directory.GetFiles(p.Path(".blackbox", "sessions")));
        var lines = File.ReadAllLines(file);
        Assert.Equal(Session.Length, lines.Length);
        Assert.False(File.Exists(p.Path(".blackbox", "errors.log")), File.Exists(p.Path(".blackbox", "errors.log")) ? File.ReadAllText(p.Path(".blackbox", "errors.log")) : "");

        var text = File.ReadAllText(file);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789", text);
        Assert.Contains("[REDACTED:github_token]", text);

        var kinds = lines.Select(l => JsonNode.Parse(l)!["kind"]?.GetValue<string>() ?? "-");
        Assert.Equal(["SessionStarted", "PromptSubmitted", "-", "FileModified", "FileRead", "CommandExecuted", "TestCompleted", "AgentStopped", "SessionEnded"], kinds);
        Assert.All(lines.Skip(2), l => Assert.Equal(1, JsonNode.Parse(l)!["prompt_seq"]!.GetValue<int>()));

        var (code, output, _) = Capture(() => Program.Verify(p.Root));
        Assert.Equal(0, code);
        Assert.Contains("OK    c30ed484-a50f-4968-bf3b-ee3c762f98b0  (9 records)", output);
    }

    [Fact]
    public void Verify_ReportsTampering_WithExitCode1()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, false, false, null);
        foreach (var f in Session.Take(4))
        {
            Hook(p, f);
        }
        var file = Directory.GetFiles(p.Path(".blackbox", "sessions"))[0];
        var lines = File.ReadAllLines(file);
        lines[1] = lines[1].Replace("Add retry logic", "Add NO logic");
        File.WriteAllLines(file, lines);

        var (code, output, _) = Capture(() => Program.Verify(p.Root));
        Assert.Equal(1, code);
        Assert.Contains("FAIL  Session c30ed484-a50f-4968-bf3b-ee3c762f98b0: tampered at seq 1 (hash mismatch)", output);
    }

    [Fact]
    public void NotInitialized_WritesNothing_ExitsZero()
    {
        using var p = new TempProject();
        Assert.Equal(0, Hook(p, "SessionStart.json"));
        Assert.Empty(Directory.GetFileSystemEntries(p.Root));
    }

    [Fact]
    public void GarbageInput_ExitsZero_LogsError()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, false, false, null);
        var (exit, stdout, stderr) = Capture(() => Program.RunHook("PostToolUse", new StringReader("{not json"), p.Root));
        Assert.Equal(0, exit);
        Assert.Equal("", stdout + stderr);
        Assert.Contains("Json", File.ReadAllText(p.Path(".blackbox", "errors.log")));
    }

    [Fact]
    public void BrokenConfig_ExitsZero_RecordsNothing()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, false, false, null);
        File.WriteAllText(p.Path(".blackbox", "config.json"), """{"record_prompts":"maybe"}""");
        Assert.Equal(0, Hook(p, "UserPromptSubmit.json"));
        Assert.Empty(Directory.GetFiles(p.Path(".blackbox", "sessions")));
        Assert.Contains("record_prompts", File.ReadAllText(p.Path(".blackbox", "errors.log")));
    }
}

public class InstallerTests
{
    private static JsonObject Settings(TempProject p, string name = "settings.json") =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(p.Path(".claude", name)))!;

    [Fact]
    public void FreshInstall_CreatesBlackboxAndAllHooks()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, false, false, null);

        Assert.True(File.Exists(p.Path(".blackbox", "config.json")));
        Assert.Equal("errors.log\n.lock\n", File.ReadAllText(p.Path(".blackbox", ".gitignore")));
        var hooks = Settings(p)["hooks"]!.AsObject();
        Assert.Equal(Installer.Events, hooks.Select(h => h.Key));
        Assert.Equal("bbx hook PostToolUse", hooks["PostToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("*", hooks["PostToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Null(hooks["UserPromptSubmit"]![0]!["matcher"]);
        Assert.Equal(5, hooks["Stop"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void Install_PreservesExistingSettings_AndBacksUp()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".claude"));
        var original = """
            {
              // user comment
              "model": "opus",
              "permissions": { "allow": ["Bash(git status)"] },
              "hooks": {
                "PostToolUse": [ { "matcher": "Edit", "hooks": [ { "type": "command", "command": "prettier --write" } ] } ]
              }
            }
            """;
        File.WriteAllText(p.Path(".claude", "settings.json"), original);

        Installer.Install(p.Root, false, false, null);

        var s = Settings(p);
        Assert.Equal("opus", s["model"]!.GetValue<string>());
        Assert.Equal("Bash(git status)", s["permissions"]!["allow"]![0]!.GetValue<string>());
        var post = s["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Equal(2, post.Count);
        Assert.Equal("prettier --write", post[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllText(p.Path(".claude", "settings.json.bbx-backup")));
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, false, false, null);
        var first = File.ReadAllText(p.Path(".claude", "settings.json"));
        var log = Installer.Install(p.Root, false, false, null);
        Assert.Equal(first, File.ReadAllText(p.Path(".claude", "settings.json")));
        Assert.Contains(log, m => m.Contains("Nothing changed"));
    }

    [Fact]
    public void Uninstall_RemovesOnlyBbxHooks()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".claude"));
        File.WriteAllText(p.Path(".claude", "settings.json"), """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"notify-send done"}]}]}}""");
        Installer.Install(p.Root, false, false, null);
        Installer.Install(p.Root, true, false, @"C:\tools\bbx.exe");

        Installer.Uninstall(p.Root);

        var hooks = Settings(p)["hooks"]!.AsObject();
        Assert.Equal(["Stop"], hooks.Select(h => h.Key));
        Assert.Equal("notify-send done", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Null(Settings(p, "settings.local.json")["hooks"]);
        Assert.True(Directory.Exists(p.Path(".blackbox")));
    }

    [Fact]
    public void Local_UsesAbsolutePath_AndIgnoresLocalSettings()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, true, false, @"C:\Users\me\tools\bbx.exe");
        var cmd = Settings(p, "settings.local.json")["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal("\"C:/Users/me/tools/bbx.exe\" hook SessionStart", cmd);
        Assert.True(Installer.IsOurs(new JsonObject { ["command"] = cmd }));
        Assert.False(File.Exists(p.Path(".claude", "settings.json")));
        Assert.Contains(".claude/settings.local.json", File.ReadAllLines(p.Path(".gitignore")));
    }

    [Fact]
    public void Private_AddsBlackboxToGitignore_Once()
    {
        using var p = new TempProject();
        File.WriteAllText(p.Path(".gitignore"), "bin/");
        Installer.Install(p.Root, false, true, null);
        Installer.Install(p.Root, false, true, null);
        Assert.Equal(["bin/", "/.blackbox/"], File.ReadAllLines(p.Path(".gitignore")));
    }

    [Theory]
    [InlineData("bbx hook PostToolUse", true)]
    [InlineData("\"C:/x y/bbx.exe\" hook Stop", true)]
    [InlineData("/usr/local/bin/bbx hook SessionEnd", true)]
    [InlineData("prettier --write", false)]
    [InlineData("bbx report latest", false)]
    public void IsOurs(string command, bool expected) =>
        Assert.Equal(expected, Installer.IsOurs(new JsonObject { ["command"] = command }));
}
