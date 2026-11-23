using System.Diagnostics;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class FreshnessTests
{
    private static SessionAnalysis Analyze(TempProject p, BlackboxConfig? config = null) =>
        new(SessionLog.LoadAll(p.Root).Single(), config ?? new BlackboxConfig());

    [Fact]
    public void NoTests_Unverified()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("x").Edit("src/a.cs", "int a = 1;");
        var a = Analyze(p);
        Assert.Equal(VerificationState.Unverified, a.Freshness.State);
        Assert.Contains(a.Anomalies, x => x.Kind == "no-tests");
    }

    [Fact]
    public void TestAfterChange_Verified_WithHonestWording()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("x").Edit("src/a.cs", "int a = 1;").Test();
        var a = Analyze(p);
        Assert.Equal(VerificationState.Verified, a.Freshness.State);
        Assert.Contains("not that the code is correct", a.Freshness.Message);
        Assert.DoesNotContain(a.Anomalies, x => x.Kind == "no-tests");
    }

    [Fact]
    public void ChangeAfterPassingTest_Stale_WithTimes()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("x").Edit("src/a.cs", "int a = 1;").At(32, 25).Test().At(32, 45).Edit("src/a.cs", "int a = 2;");
        var a = Analyze(p);
        Assert.Equal(VerificationState.Stale, a.Freshness.State);
        Assert.Equal("Source changed at 10:32:45 after last passing test at 10:32:25. Verification is STALE.", a.Freshness.Message);
    }

    [Fact]
    public void DocsChangeAfterTest_StaysVerified_UnlessConfigured()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Edit("src/a.cs", "int a = 1;").Test().Edit("docs/guide.md", "Some text here").Edit("README.md", "More text here");
        Assert.Equal(VerificationState.Verified, Analyze(p).Freshness.State);
        Assert.Equal(VerificationState.Stale, Analyze(p, new BlackboxConfig { NonSourcePaths = [] }).Freshness.State);
    }

    [Fact]
    public void LastTestFailed_Failed()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Edit("src/a.cs", "int a = 1;").Test().Test(pass: false);
        var a = Analyze(p);
        Assert.Equal(VerificationState.Failed, a.Freshness.State);
        Assert.Equal(1, a.Errors);
        Assert.Contains(a.Anomalies, x => x.Kind == "failed-tool-call");
    }

    [Fact]
    public void FixedAfterFailure_Verified()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Edit("src/a.cs", "int a = 1;").Test(pass: false).Edit("src/a.cs", "int a = 2;").Test();
        Assert.Equal(VerificationState.Verified, Analyze(p).Freshness.State);
    }
}

public class AnalysisTests
{
    [Theory]
    [InlineData("Passed!  - Failed:     0, Passed:    12, Skipped:     1, Total:    13 - x.dll", 12, 0, 1, 13)]
    [InlineData("Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2\nFailed!  - Failed: 1, Passed: 3, Skipped: 0, Total: 4", 5, 1, 0, 6)]
    [InlineData("Test summary: total: 133, failed: 0, succeeded: 133, skipped: 0, duration: 2.1s", 133, 0, 0, 133)]
    public void ParsesTestCounts(string output, int passed, int failed, int skipped, int total) =>
        Assert.Equal(new TestCounts(passed, failed, skipped, total), SessionAnalysis.ParseTestCounts(output));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12 passing (3s)")]
    public void UnknownOutput_HasNoCounts(string? output) => Assert.Null(SessionAnalysis.ParseTestCounts(output));

    [Fact]
    public void GroupsActionsUnderPrompts_AndCountsFiles()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Start().Edit("src/early.cs", "class Early {}")
            .Prompt("first").Edit("src/a.cs", "int a = 1;").Edit("src/a.cs", "int a = 2;").Edit("src/a.cs", "int a = 3;")
            .Prompt("second").Command("rm -rf bin", kind: "FileDeleted").Test().Stop();
        var a = new SessionAnalysis(SessionLog.LoadAll(p.Root).Single(), new BlackboxConfig());

        Assert.Equal(3, a.Groups.Count);
        Assert.Null(a.Groups[0].Prompt);
        Assert.Equal("first", a.Groups[1].Prompt!.O("data").S("prompt"));
        Assert.Equal(3, a.Groups[1].Actions.Count);
        Assert.Equal(2, a.PromptCount);
        Assert.Equal(6, a.ToolCalls);
        var file = a.Files.First();
        Assert.Equal(("src/a.cs", 3, 3), (file.File, file.Edits, file.Added));
        Assert.Contains(a.Anomalies, x => x.Kind == "oscillation" && x.Message == "src/a.cs was edited 3 times");
        Assert.Contains(a.Anomalies, x => x.Kind == "deleted-files" && x.Message.Contains("rm -rf bin"));
        Assert.Equal("main", a.Session.Branch);
    }

    [Fact]
    public void Describe_IncludesTestCountsAndExitCodes()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Test().Command("git push", success: false);
        var s = SessionLog.LoadAll(p.Root).Single();
        Assert.Equal("Bash `dotnet test` (exit 0) · 5 passed, 0 failed, 0 skipped", SessionAnalysis.Describe(s.Records[0]));
        Assert.Equal("Bash `git push` (exit 1)", SessionAnalysis.Describe(s.Records[1]));
    }

    [Fact]
    public void Find_ByPrefixLatestAndErrors()
    {
        using var p = new TempProject();
        new SessionBuilder(p, "aaa111").At(1).Prompt("x");
        new SessionBuilder(p, "aaa222").At(5).Prompt("y");
        new SessionBuilder(p, "bbb333").At(3).Prompt("z");
        var all = SessionLog.LoadAll(p.Root);
        Assert.Equal("aaa222", SessionLog.Find(all, null).Id);
        Assert.Equal("bbb333", SessionLog.Find(all, "bbb").Id);
        Assert.Contains("matches 2 sessions", Assert.Throws<InvalidOperationException>(() => SessionLog.Find(all, "aaa")).Message);
        Assert.Throws<InvalidOperationException>(() => SessionLog.Find(all, "zzz"));
        Assert.Throws<InvalidOperationException>(() => SessionLog.Find([], null));
    }

    [Fact]
    public void Load_ReportsTampering()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("x").Prompt("y");
        var file = SessionLog.LoadAll(p.Root).Single().File;
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"x\"", "\"z\""));
        var s = SessionLog.Load(file);
        Assert.False(s.Verify.Ok);
        Assert.Equal(2, s.Records.Count);
    }
}

public class ProvenanceTests
{
    [Fact]
    public void Blame_AttributesAgentLines_CountsTheRestAsHuman()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("Add retry logic").Edit("src/Order.cs", "public class Order", "{", "    int retries = 3;", "}");
        File.AppendAllText(p.Path("src", "Order.cs"), "// written by hand later\n");

        var r = Provenance.Blame(p.Root, "src/Order.cs", SessionLog.LoadAll(p.Root));
        Assert.Equal(2, r.AiLines);
        Assert.Equal(1, r.HumanLines);
        Assert.Equal(67, r.AiPercent);
        Assert.True(r.Lines[1].Trivial);
        Assert.Equal("Add retry logic", r.Lines[2].Origin!.Prompt!.O("data").S("prompt"));
        Assert.Null(r.Lines[4].Origin);
        Assert.StartsWith("File changed outside recorded sessions", r.FileNote);
    }

    [Fact]
    public void Blame_ModifiedLine_IsNoLongerAi_MovedLine_IsStillFound()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("x").Edit("a.cs", "int first = 1;", "int second = 2;");
        File.WriteAllText(p.Path("a.cs"), "int second = 2;\nint first = 100;\n");
        var r = Provenance.Blame(p.Root, "a.cs", SessionLog.LoadAll(p.Root));
        Assert.NotNull(r.Lines[0].Origin);
        Assert.Null(r.Lines[1].Origin);
    }

    [Fact]
    public void Blame_IgnoresMatchesFromOtherFiles()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("x").Edit("a.cs", "using System.Text;");
        File.WriteAllText(p.Path("b.cs"), "using System.Text;\n");
        var r = Provenance.Blame(p.Root, "b.cs", SessionLog.LoadAll(p.Root));
        Assert.Equal(0, r.AiLines);
        Assert.Contains("No recorded agent edits", r.FileNote);
    }

    [Fact]
    public void Blame_LatestEditWins()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("first").Edit("a.cs", "int x = 1;").Prompt("second").Edit("a.cs", "int x = 1;");
        var r = Provenance.Blame(p.Root, "a.cs", SessionLog.LoadAll(p.Root));
        Assert.Equal("second", r.Lines[0].Origin!.Prompt!.O("data").S("prompt"));
        Assert.StartsWith("File unchanged since", r.FileNote);
    }
}

public class InvestigatorTests
{
    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        psi.Environment["GIT_AUTHOR_DATE"] = psi.Environment["GIT_COMMITTER_DATE"] = "2026-11-24T10:30:00Z";
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, proc.StandardError.ReadToEnd());
    }

    [Fact]
    public void MatchesExactLikelyAndNone()
    {
        using var p = new TempProject();
        Git(p.Root, "init", "-q", "-b", "main");
        Git(p.Root, "config", "user.email", "t@example.com");
        Git(p.Root, "config", "user.name", "t");
        Git(p.Root, "config", "core.autocrlf", "false");

        new SessionBuilder(p).At(5).Prompt("Add order service").Edit("src/Order.cs", "class Order { }")
            .Edit("src/Other.cs", "class Other { }").Test();
        File.WriteAllText(p.Path("src", "Other.cs"), "class Other { int changedByHand; }\n");
        File.WriteAllText(p.Path("notes.txt"), "human notes\n");
        Git(p.Root, "add", "src", "notes.txt");
        Git(p.Root, "commit", "-q", "-m", "Add order service");

        var inv = Investigator.Investigate(p.Root, "HEAD", SessionLog.LoadAll(p.Root));
        Assert.Equal("Add order service", inv.Subject);
        Assert.Null(inv.ParentTime);
        Assert.Equal("exact", inv.Files.Single(f => f.File == "src/Order.cs").Confidence);
        Assert.Equal("likely", inv.Files.Single(f => f.File == "src/Other.cs").Confidence);
        Assert.Equal("none", inv.Files.Single(f => f.File == "notes.txt").Confidence);
        Assert.Equal("sess-0001", Assert.Single(inv.Sessions).Id);
    }

    [Fact]
    public void UnknownCommit_Throws()
    {
        using var p = new TempProject();
        Git(p.Root, "init", "-q");
        Assert.Throws<InvalidOperationException>(() => Investigator.Investigate(p.Root, "nope", []));
    }
}
