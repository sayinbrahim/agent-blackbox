using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class AnchorTests
{
    private static TempProject Repo()
    {
        var p = new TempProject();
        p.GitInit();
        File.WriteAllText(p.Path("README.md"), "demo\n");
        p.Git("add", "-A");
        p.Git("commit", "-q", "-m", "init");
        return p;
    }

    private static string Msg(TempProject p, string text = "feat: work\n")
    {
        var f = p.Path("msg.txt");
        File.WriteAllText(f, text);
        return f;
    }

    [Fact]
    public void StagedHeads_AreTheLastStagedRecord_NotTheWorkingTree()
    {
        using var p = Repo();
        var b = new SessionBuilder(p).Prompt("one").Edit("src/A.cs", "int a = 1;");
        p.Git("add", "-A");
        b.Prompt("two"); // recorded after staging: not part of this commit

        var head = Assert.Single(Anchors.StagedHeads(p.Root));
        var staged = SessionLog.LoadAll(p.Root).Single().Records[1];
        Assert.Equal(new Anchor("sess-0001", 1, staged.S("hash")!), head);
        Assert.Equal($"sess-0001 1 {staged.S("hash")}", Anchors.Format(head));
        Assert.Equal(head, Anchors.Parse(Anchors.Format(head)));
    }

    [Fact]
    public void NothingStaged_NoAnchors()
    {
        using var p = Repo();
        new SessionBuilder(p).Prompt("one");
        Assert.Empty(Anchors.StagedHeads(p.Root));
    }

    [Fact]
    public void AddToMessage_AppendsTrailers_Once()
    {
        using var p = Repo();
        new SessionBuilder(p).Prompt("one");
        p.Git("add", "-A");
        var msg = Msg(p);

        Assert.Equal(1, Anchors.AddToMessage(p.Root, msg));
        Assert.Equal(1, Anchors.AddToMessage(p.Root, msg));
        var text = File.ReadAllText(msg);
        Assert.StartsWith("feat: work\n", text);
        Assert.Single(text.Split('\n'), l => l.StartsWith("Blackbox-Head: sess-0001 0 sha256:", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_MatchingAnchors_AreOk_RewrittenChainIsCaught()
    {
        using var p = Repo();
        new SessionBuilder(p).Prompt("REQ-1 add order").Edit("src/A.cs", "int a = 1;");
        p.Git("add", "-A");
        var msg = Msg(p);
        Anchors.AddToMessage(p.Root, msg);
        p.Git("commit", "-q", "-F", msg);

        var ok = Anchors.Verify(p.Root, SessionLog.LoadAll(p.Root));
        var a = Assert.Single(ok);
        Assert.Null(a.Problem);

        // Rewrite history the way an attacker with write access could: a fresh, internally valid chain.
        File.Delete(Directory.GetFiles(p.Path(".blackbox", "sessions")).Single());
        new SessionBuilder(p).Prompt("REQ-1 add order, nothing else to see").Edit("src/A.cs", "int a = 1;");
        var logs = SessionLog.LoadAll(p.Root);
        Assert.True(logs.Single().Verify.Ok);

        var bad = Assert.Single(Anchors.Verify(p.Root, logs));
        Assert.Equal("record #1 differs from the one anchored in this commit", bad.Problem);
    }

    [Fact]
    public void Verify_MissingSessionOrRecord_IsReported()
    {
        using var p = Repo();
        new SessionBuilder(p).Prompt("one").Prompt("two");
        p.Git("add", "-A");
        var msg = Msg(p);
        Anchors.AddToMessage(p.Root, msg);
        p.Git("commit", "-q", "-F", msg);

        var file = Directory.GetFiles(p.Path(".blackbox", "sessions")).Single();
        File.WriteAllLines(file, File.ReadAllLines(file).Take(1)); // truncate: a deleted tail
        Assert.Equal("record #1 is missing", Assert.Single(Anchors.Verify(p.Root, SessionLog.LoadAll(p.Root))).Problem);

        File.Delete(file);
        Assert.Equal("session log is missing", Assert.Single(Anchors.Verify(p.Root, SessionLog.LoadAll(p.Root))).Problem);
    }

    [Fact]
    public void Verify_OutsideGit_HasNoAnchors()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("one");
        Assert.Empty(Anchors.Verify(p.Root, SessionLog.LoadAll(p.Root)));
    }

    [Fact]
    public void InstallHook_WritesOurs_KeepsSomeoneElses()
    {
        using var p = Repo();
        var hook = Anchors.InstallHook(p.Root, "bbx");
        Assert.Contains("agent-blackbox", File.ReadAllText(hook));
        Assert.Equal(hook, Anchors.InstallHook(p.Root, "/opt/bbx")); // ours: replaced
        Assert.Contains("\"/opt/bbx\" anchor --message-file", File.ReadAllText(hook));

        File.WriteAllText(hook, "#!/bin/sh\necho custom\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Anchors.InstallHook(p.Root, "bbx"));
        Assert.Contains("already has a prepare-commit-msg hook", ex.Message);
        Assert.Equal("#!/bin/sh\necho custom\n", File.ReadAllText(hook));
    }
}
