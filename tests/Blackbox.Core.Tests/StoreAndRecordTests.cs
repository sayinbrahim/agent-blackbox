using System.Text.Json.Nodes;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class SessionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 15, 2, TimeSpan.Zero);

    private static JsonObject Rec(string evt, string session = "abc") => new() { ["v"] = 1, ["session"] = session, ["event"] = evt, ["data"] = new JsonObject() };

    [Fact]
    public void Append_AssignsSeqPrevAndPromptSeq()
    {
        using var p = new TempProject();
        var store = new SessionStore(p.Path(".blackbox"));
        var a = store.Append(Rec("SessionStart"), Now);
        var b = store.Append(Rec("UserPromptSubmit"), Now);
        var c = store.Append(Rec("PostToolUse"), Now);
        var d = store.Append(Rec("UserPromptSubmit"), Now);
        var e = store.Append(Rec("PostToolUse"), Now);

        Assert.Equal([0, 1, 2, 3, 4], new[] { a, b, c, d, e }.Select(r => r["seq"]!.GetValue<int>()));
        Assert.Equal(HashChain.Genesis, a["prev"]!.GetValue<string>());
        Assert.Equal(a["hash"]!.GetValue<string>(), b["prev"]!.GetValue<string>());
        Assert.Null(a["prompt_seq"]);
        Assert.Equal(1, c["prompt_seq"]!.GetValue<int>());
        Assert.Equal(3, e["prompt_seq"]!.GetValue<int>());

        var file = store.FindSessionFile("abc")!;
        Assert.Equal("2026-11-24T10-15-02Z_abc.jsonl", Path.GetFileName(file));
        Assert.True(ChainVerifier.VerifyFile(file).Ok);
    }

    [Fact]
    public void Sessions_GetSeparateFiles()
    {
        using var p = new TempProject();
        var store = new SessionStore(p.Path(".blackbox"));
        store.Append(Rec("SessionStart", "one"), Now);
        store.Append(Rec("SessionStart", "two"), Now);
        Assert.Equal(2, store.AllSessionFiles().Count());
    }

    [Fact]
    public void UnsafeSessionIds_AreSanitized()
    {
        Assert.Equal("___etc_passwd", SessionStore.SafeId("../etc/passwd"));
        Assert.Equal("unknown", SessionStore.SafeId(null));
    }

    [Fact]
    public void ParallelAppends_KeepTheChainIntact()
    {
        using var p = new TempProject();
        var dir = p.Path(".blackbox");
        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            new SessionStore(dir).Append(Rec("PostToolUse"), Now, TimeSpan.FromSeconds(10)));
        var result = ChainVerifier.VerifyFile(new SessionStore(dir).FindSessionFile("abc")!);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(40, result.Records);
    }

    [Fact]
    public void LongRecords_AcrossReadChunks()
    {
        using var p = new TempProject();
        var store = new SessionStore(p.Path(".blackbox"));
        for (var i = 0; i < 3; i++)
        {
            var r = Rec("PostToolUse");
            r["data"]!["big"] = new string('x', 20_000);
            store.Append(r, Now);
        }
        Assert.Equal(3, ChainVerifier.VerifyFile(store.FindSessionFile("abc")!).Records);
    }
}

public class RecordBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 16, 41, 123, TimeSpan.Zero);

    private static JsonObject Build(TempProject p, string fixture, BlackboxConfig? config = null) =>
        new RecordBuilder(p.Root, config ?? new BlackboxConfig(), "0.1.0-test").Build(Fixtures.LoadObject(fixture, p.Root), "", Now);

    [Fact]
    public void Edit_RecordsDiffHashesAndRelativePath()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path("src"));
        File.WriteAllText(p.Path("src", "sample.txt"), "line one\nline two changed\nline three\n");

        var r = Build(p, "PostToolUse.edit.json");
        var d = r["data"]!;
        Assert.Equal("FileModified", r["kind"]!.GetValue<string>());
        Assert.Equal("agent", r["actor"]!.GetValue<string>());
        Assert.Equal("Edit", r["tool"]!.GetValue<string>());
        Assert.Equal("2026-11-24T10:16:41.123Z", r["ts"]!.GetValue<string>());
        Assert.Equal("src/sample.txt", d["file"]!.GetValue<string>());
        Assert.Equal("edit", d["op"]!.GetValue<string>());
        Assert.Equal(2, d["added_lines"]!.GetValue<int>());
        Assert.Equal(1, d["removed_lines"]!.GetValue<int>());
        Assert.Equal([LineHasher.Hash("line two changed"), LineHasher.Hash("line three")], d["added_line_hashes"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Equal("sha256:" + HashChain.Sha256Hex("line one\nline two\n"), d["before_hash"]!.GetValue<string>());
        Assert.Equal("sha256:" + HashChain.Sha256Hex(File.ReadAllBytes(p.Path("src", "sample.txt"))), d["after_hash"]!.GetValue<string>());
        Assert.Contains("@@ -1,2 +1,3 @@", d["diff"]!.GetValue<string>());
        Assert.Equal("src/sample.txt", d["input"]!["file"]!.GetValue<string>());
    }

    [Fact]
    public void NoAbsolutePathsOrUserDirs_EndUpInTheRecord()
    {
        using var p = new TempProject();
        foreach (var f in Directory.GetFiles(Fixtures.Dir, "*.json"))
        {
            var text = CanonicalJson.Serialize(Build(p, Path.GetFileName(f)));
            Assert.DoesNotContain("transcript", text);
            Assert.DoesNotContain("scratchpad", text);
            Assert.DoesNotContain(p.Root.Replace('\\', '/'), text);
            Assert.DoesNotContain("Users", text);
        }
    }

    [Fact]
    public void WriteCreate_UsesContentAsAddedLines()
    {
        using var p = new TempProject();
        var r = Build(p, "PostToolUse.write-create.json");
        Assert.Equal("FileCreated", r["kind"]!.GetValue<string>());
        Assert.Equal("create", r["data"]!["op"]!.GetValue<string>());
        Assert.Equal(2, r["data"]!["added_lines"]!.GetValue<int>());
        Assert.Null(r["data"]!["before_hash"]);
    }

    [Fact]
    public void IgnoredPath_RecordsOnlyThatTheFileChanged()
    {
        using var p = new TempProject();
        var r = Build(p, "PostToolUse.edit.json", new BlackboxConfig { IgnorePaths = ["src/*.txt"] });
        var d = r["data"]!.AsObject();
        Assert.True(d["ignored"]!.GetValue<bool>());
        Assert.False(d.ContainsKey("diff"));
        Assert.False(d.ContainsKey("added_line_hashes"));
        Assert.False(d.ContainsKey("before_hash"));
    }

    [Fact]
    public void Read_IsFileRead_WithoutContent()
    {
        using var p = new TempProject();
        var r = Build(p, "PostToolUse.read.json");
        Assert.Equal("FileRead", r["kind"]!.GetValue<string>());
        Assert.DoesNotContain("line one", CanonicalJson.Serialize(r));
    }

    [Fact]
    public void BashFailure_HasExitCodeAndError()
    {
        using var p = new TempProject();
        var r = Build(p, "PostToolUseFailure.bash.json");
        var d = r["data"]!;
        Assert.Equal("CommandExecuted", r["kind"]!.GetValue<string>());
        Assert.False(d["success"]!.GetValue<bool>());
        Assert.Equal(3, d["exit_code"]!.GetValue<int>());
        Assert.Equal("echo hello; exit 3", d["input"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void BashTest_IsTestCompleted_UnknownFieldsKept()
    {
        using var p = new TempProject();
        var r = Build(p, "PostToolUse.bash-test.json");
        Assert.Equal("TestCompleted", r["kind"]!.GetValue<string>());
        Assert.Equal(0, r["data"]!["exit_code"]!.GetValue<int>());
        Assert.Contains("Passed:    12", r["data"]!["output_tail"]!.GetValue<string>());
        Assert.Equal("kept in raw_extra", r["raw_extra"]!["future_field"]!.GetValue<string>());
    }

    [Fact]
    public void Prompt_IsRedacted_AndHonoursRecordPrompts()
    {
        using var p = new TempProject();
        var full = Build(p, "UserPromptSubmit.json");
        Assert.Equal("PromptSubmitted", full["kind"]!.GetValue<string>());
        Assert.Equal("human", full["actor"]!.GetValue<string>());
        Assert.Equal("Add retry logic to order submission. My token is [REDACTED:github_token]", full["data"]!["prompt"]!.GetValue<string>());

        var hashed = Build(p, "UserPromptSubmit.json", new BlackboxConfig { RecordPrompts = "hash" });
        Assert.Null(hashed["data"]!["prompt"]);
        Assert.StartsWith("sha256:", hashed["data"]!["prompt_hash"]!.GetValue<string>());

        var off = Build(p, "UserPromptSubmit.json", new BlackboxConfig { RecordPrompts = "off" });
        Assert.Null(off["data"]!["prompt"]);
        Assert.Null(off["data"]!["prompt_hash"]);
    }

    [Fact]
    public void SessionStart_RecordsGitInfoAndVersion()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".git", "refs", "heads"));
        File.WriteAllText(p.Path(".git", "HEAD"), "ref: refs/heads/milestone/1\n");
        Directory.CreateDirectory(p.Path(".git", "refs", "heads", "milestone"));
        File.WriteAllText(p.Path(".git", "refs", "heads", "milestone", "1"), "abc123\n");

        var r = Build(p, "SessionStart.json");
        var d = r["data"]!;
        Assert.Equal("SessionStarted", r["kind"]!.GetValue<string>());
        Assert.Equal("system", r["actor"]!.GetValue<string>());
        Assert.Equal("milestone/1", d["git_branch"]!.GetValue<string>());
        Assert.Equal("abc123", d["git_head"]!.GetValue<string>());
        Assert.Equal("0.1.0-test", d["bbx_version"]!.GetValue<string>());
        Assert.Equal(".", d["cwd"]!.GetValue<string>());
    }

    [Fact]
    public void FileOutsideProject_KeepsOnlyFileName()
    {
        using var p = new TempProject();
        var builder = new RecordBuilder(p.Path("inner"), new BlackboxConfig(), "x");
        Assert.Equal("<outside>/secret.txt", builder.Rel(p.Path("secret.txt"), out var outside));
        Assert.True(outside);
    }
}
