using System.Text.RegularExpressions;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

public class CheckTests
{
    private static readonly Regex Req = new(@"REQ-\d+");

    private static CheckResult Run(TempProject p, Regex? trace = null, bool tests = false, string? baseRef = null) =>
        Checker.Run(p.Root, SessionLog.LoadAll(p.Root), new BlackboxConfig(), new CheckOptions(trace, tests, baseRef));

    [Fact]
    public void NoRules_ListsAiFiles_AndPasses()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("add order").Edit("src/Order.cs", "class Order { }", "// done");
        var r = Run(p);
        var f = Assert.Single(r.Files);
        Assert.Equal("src/Order.cs", f.File);
        Assert.Equal(2, f.AiLines);
        Assert.True(r.Passed);
    }

    [Fact]
    public void RequireTrace_PassesWhenThePromptHasAnId()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("REQ-12: add order").Edit("src/Order.cs", "class Order { }");
        var r = Run(p, Req);
        Assert.True(r.Passed);
        Assert.Equal(["REQ-12"], r.Files.Single().TraceIds);
    }

    [Fact]
    public void RequireTrace_AnEarlierPromptInTheSessionCounts()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("Work on REQ-7 today").Edit("src/A.cs", "int a = 1;").Prompt("now fix the typo").Edit("src/B.cs", "int b = 2;");
        var r = Run(p, Req);
        Assert.True(r.Passed);
        Assert.All(r.Files, f => Assert.Equal(["REQ-7"], f.TraceIds));
    }

    [Fact]
    public void RequireTrace_FailsForUntracedCode_InDo178cWording()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("add order").Edit("src/Order.cs", "class Order { }");
        new SessionBuilder(p, "sess-0002").Prompt("REQ-3 add customer").Edit("src/Customer.cs", "class Customer { }");
        var r = Run(p, Req);
        Assert.False(r.Passed);
        Assert.Equal(1, r.Untraced);
        var bad = r.Files.Single(f => f.File == "src/Order.cs");
        Assert.False(bad.Traced);
        Assert.Empty(bad.TraceIds);
        Assert.Equal("no requirement ID in the prompts behind it", bad.TraceIssue);
        Assert.Equal("Untraced AI code: 1 file (no requirement link)", r.Summary()[0]);
    }

    [Fact]
    public void RequireTrace_DoesNotJudgeDocs()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("write the notes").Edit("docs/notes.md", "Some notes here.").Edit("README.md", "Read me first.");
        var r = Run(p, Req);
        Assert.Equal(2, r.Files.Count);
        Assert.All(r.Files, f => Assert.Null(f.Traced));
        Assert.True(r.Passed);
    }

    [Fact]
    public void RequireTrace_SaysWhenPromptsWereNotRecorded()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Add("UserPromptSubmit", "PromptSubmitted", null, new() { ["prompt_hash"] = "sha256:00" }, "human").Edit("src/Order.cs", "class Order { }");
        var r = Run(p, Req);
        Assert.False(r.Passed);
        Assert.Equal("prompt not recorded (record_prompts is hash or off)", r.Files.Single().TraceIssue);
    }

    [Fact]
    public void RequireTests_TestRunOrTestFileInTheSessionCounts()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("a").Edit("src/A.cs", "int a = 1;").Test();
        new SessionBuilder(p, "sess-0002").Prompt("b").Edit("src/B.cs", "int b = 1;").Edit("tests/BTests.cs", "void Test() { }");
        new SessionBuilder(p, "sess-0003").Prompt("c").Edit("src/C.cs", "int c = 1;");
        new SessionBuilder(p, "sess-0004").Prompt("d").Edit("docs/notes.md", "Some notes here.");
        var r = Run(p, tests: true);

        Assert.False(r.Passed);
        Assert.Equal(1, r.Untested);
        Assert.False(r.Files.Single(f => f.File == "src/C.cs").HasTests);
        Assert.True(r.Files.Single(f => f.File == "src/A.cs").HasTests);
        Assert.True(r.Files.Single(f => f.File == "src/B.cs").HasTests);
        Assert.Null(r.Files.Single(f => f.File == "docs/notes.md").HasTests);   // not source: rule doesn't apply
        Assert.Null(r.Files.Single(f => f.File == "tests/BTests.cs").HasTests); // a test file needs no test
    }

    [Fact]
    public void LinesChangedByHand_AreNotAiCode()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("add order").Edit("src/Order.cs", "class Order { }");
        File.WriteAllText(p.Path("src", "Order.cs"), "class Order { int id; }\n");
        var r = Run(p, Req);
        Assert.Empty(r.Files);
        Assert.True(r.Passed);
    }

    [Fact]
    public void Base_OnlyLinesAddedSinceTheBaseCount()
    {
        using var p = new TempProject();
        p.GitInit();
        new SessionBuilder(p).Prompt("old work").Edit("src/Old.cs", "class Old { }");
        p.Git("add", "-A");
        p.Git("commit", "-q", "-m", "base");
        p.Git("branch", "base");
        new SessionBuilder(p, "sess-0002").Prompt("REQ-9 new work").Edit("src/New.cs", "class New { }");
        p.Git("add", "-A");
        p.Git("commit", "-q", "-m", "feature");

        var r = Run(p, Req, baseRef: "base");
        var f = Assert.Single(r.Files);
        Assert.Equal("src/New.cs", f.File);
        Assert.True(r.Passed);
        Assert.False(Run(p, Req).Passed); // without --base the untraced old file counts too
    }

    [Fact]
    public void IgnoredFiles_AreSkipped_ByConfigAndByGit()
    {
        using var p = new TempProject();
        p.GitInit();
        File.WriteAllText(p.Path(".gitignore"), "drafts/\n");
        p.Git("add", "-A");
        p.Git("commit", "-q", "-m", "base");
        p.Git("branch", "base");
        new SessionBuilder(p).Prompt("work").Edit("drafts/plan.cs", "int plan = 1;").Edit("secrets/key.cs", "int key = 1;").Edit("src/A.cs", "int a = 1;");

        var r = Checker.Run(p.Root, SessionLog.LoadAll(p.Root), new BlackboxConfig { IgnorePaths = ["secrets/**"] }, new CheckOptions(null, false, "base"));
        Assert.Equal(["src/A.cs"], r.Files.Select(f => f.File));
    }

    [Fact]
    public void BrokenChain_FailsTheCheck()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("REQ-1 a").Edit("src/A.cs", "int a = 1;");
        var file = Directory.GetFiles(p.Path(".blackbox", "sessions")).Single();
        File.WriteAllText(file, File.ReadAllText(file).Replace("REQ-1 a", "REQ-1 b"));
        var r = Run(p, Req);
        Assert.False(r.Passed);
        Assert.Single(r.BrokenChains);
    }

    [Theory]
    [InlineData("tests/Orders.Tests/OrderServiceTests.cs", true)]
    [InlineData("src/order.test.ts", true)]
    [InlineData("spec/order_spec.rb", true)]
    [InlineData("src/__tests__/a.js", true)]
    [InlineData("src/Orders/OrderService.cs", false)]
    [InlineData("src/Contest.cs", false)]
    public void TestPaths(string path, bool expected) => Assert.Equal(expected, Checker.IsTestPath(path));
}
