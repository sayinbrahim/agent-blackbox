using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class HookRecorderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FindRoot_WalksUpFromSubdirectory()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".blackbox"));
        Directory.CreateDirectory(p.Path("src", "deep"));
        Assert.Equal(p.Root, HookRecorder.FindRoot(p.Path("src", "deep")));
        Assert.Null(HookRecorder.FindRoot(null));
    }

    [Fact]
    public void Record_WithoutBlackbox_ReturnsNull()
    {
        using var p = new TempProject();
        Assert.Null(HookRecorder.Record("Stop", Fixtures.Load("Stop.json", p.Root), p.Root, "x", Now));
    }

    [Fact]
    public void Record_FallsBackToCwdFromPayload()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".blackbox"));
        Assert.Equal(p.Root, HookRecorder.Record("Stop", Fixtures.Load("Stop.json", p.Root), projectDir: null, "x", Now));
        Assert.Single(new SessionStore(p.Path(".blackbox")).AllSessionFiles());
    }

    [Fact]
    public void Record_BadConfig_ThrowsRecorderExceptionWithRoot()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".blackbox"));
        File.WriteAllText(p.Path(".blackbox", "config.json"), "[]");
        var ex = Assert.Throws<RecorderException>(() => HookRecorder.Record("Stop", Fixtures.Load("Stop.json", p.Root), p.Root, "x", Now));
        Assert.Equal(p.Root, ex.Root);

        HookRecorder.LogError(ex.Root, ex.InnerException!, Now);
        Assert.Contains("InvalidDataException", File.ReadAllText(p.Path(".blackbox", "errors.log")));
    }

    [Fact]
    public void GitInfo_DetachedHead_AndPackedRefs()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".git"));
        File.WriteAllText(p.Path(".git", "HEAD"), "0123abcd\n");
        Assert.Equal((null, "0123abcd"), GitInfo.Read(p.Root));

        File.WriteAllText(p.Path(".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(p.Path(".git", "packed-refs"), "# pack-refs\nfeedbeef refs/heads/main\n");
        Assert.Equal(("main", "feedbeef"), GitInfo.Read(p.Root));

        File.Delete(p.Path(".git", "packed-refs"));
        Assert.Equal(("main", null), GitInfo.Read(p.Root));
        Assert.Equal((null, null), GitInfo.Read(p.Path("nowhere")));
    }

    [Fact]
    public void GitInfo_WorktreeGitFile()
    {
        using var p = new TempProject();
        var main = p.Path("main", ".git");
        Directory.CreateDirectory(Path.Combine(main, "worktrees", "wt"));
        Directory.CreateDirectory(Path.Combine(main, "refs", "heads"));
        File.WriteAllText(Path.Combine(main, "refs", "heads", "feature"), "cafe\n");
        File.WriteAllText(Path.Combine(main, "worktrees", "wt", "HEAD"), "ref: refs/heads/feature\n");
        File.WriteAllText(Path.Combine(main, "worktrees", "wt", "commondir"), "../..\n");
        Directory.CreateDirectory(p.Path("wt"));
        File.WriteAllText(p.Path("wt", ".git"), "gitdir: " + Path.Combine(main, "worktrees", "wt") + "\n");
        Assert.Equal(("feature", "cafe"), GitInfo.Read(p.Path("wt")));
    }
}
