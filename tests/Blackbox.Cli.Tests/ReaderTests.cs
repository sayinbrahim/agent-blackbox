using Blackbox.Cli;
using Blackbox.Tests.Shared;

namespace Blackbox.Cli.Tests;

public class ReaderTests
{
    private static readonly Ansi NoColor = new(false);

    private static TempProject Recorded()
    {
        var p = new TempProject();
        Installer.Install(p.Root, false, false, null);
        new SessionBuilder(p).Start("feature/x").Prompt("Add <retry> & backoff")
            .Edit("src/Order.cs", "public class Order", "{", "    int retries = 3;", "}")
            .At(10).Test().At(11).Edit("src/Order.cs", "public class Order", "{", "    int retries = 5;", "}")
            .Command("git push", success: false).Stop();
        return p;
    }

    private static string Run(Func<TextWriter, int> f, int expectedExit = 0)
    {
        using var w = new StringWriter();
        Assert.Equal(expectedExit, f(w));
        return w.ToString();
    }

    [Fact]
    public void Check_FailsUntracedCode_ThenPassesWithoutRules()
    {
        using var p = Recorded();
        var fail = Run(w => Readers.Check(p.Root, @"REQ-\d+", true, null, w, NoColor), expectedExit: 1);
        Assert.Contains("src/Order.cs", fail);
        Assert.Contains("✖ untraced", fail);
        Assert.Contains("no requirement ID in the prompts behind it (session sess-000 prompt #1)", fail);
        Assert.Contains("FAIL  Untraced AI code: 1 file (no requirement link)", fail);
        Assert.DoesNotContain("without test evidence", fail); // the session ran dotnet test

        var list = Run(w => Readers.Check(p.Root, null, false, null, w, NoColor));
        Assert.Contains("PASS  No rules given; listing only.", list);
    }

    [Fact]
    public void Check_BadPattern_IsAnError()
    {
        using var p = Recorded();
        Assert.Throws<InvalidOperationException>(() => Readers.Check(p.Root, "REQ-(", false, null, TextWriter.Null, NoColor));
    }

    [Fact]
    public void Sessions_ListsOneRow()
    {
        using var p = Recorded();
        var o = Run(w => Readers.Sessions(p.Root, w, NoColor));
        Assert.Contains("sess-000", o);
        Assert.Contains("STALE", o);
        Assert.DoesNotContain("\u001b[", o);
    }

    [Fact]
    public void Timeline_ShowsPromptActionsAndStaleWarning()
    {
        using var p = Recorded();
        var o = Run(w => Readers.Timeline(p.Root, null, w, NoColor));
        Assert.Contains("branch feature/x", o);
        Assert.Contains("#1 10:00:01 Add <retry> & backoff", o);
        Assert.Contains("✔ Edit src/Order.cs (+4 -0)", o);
        Assert.Contains("✖ Bash `git push` (exit 1)", o);
        Assert.Contains("⚠ Source changed at 10:11:00 after last passing test at 10:10:00. Verification is STALE.", o);
    }

    [Fact]
    public void Colors_AreUsedWhenEnabled()
    {
        using var p = Recorded();
        Assert.Contains("\u001b[", Run(w => Readers.Timeline(p.Root, null, w, new Ansi(true))));
    }

    [Fact]
    public void Blame_FileSummaryAndLine()
    {
        using var p = Recorded();
        var summary = Run(w => Readers.Blame(p.Root, p.Path("src", "Order.cs"), w, NoColor));
        Assert.Contains("src/Order.cs: 100% AI, 0% human  (2 of 2 non-trivial lines)", summary);

        var line = Run(w => Readers.Blame(p.Root, "src/Order.cs:3", w, NoColor));
        Assert.Contains("Line 3: AI-written", line);
        Assert.Contains("Prompt  : #1 \"Add <retry> & backoff\"", line);
        Assert.Contains("Tool    : Edit at 10:11:00", line);
    }

    [Fact]
    public void Blame_Json_ListsAiLinesForEditors()
    {
        using var p = Recorded();
        var json = System.Text.Json.Nodes.JsonNode.Parse(Run(w => Readers.BlameJson(p.Root, p.Path("src", "Order.cs"), w)))!;

        Assert.Equal("src/Order.cs", json["file"]!.GetValue<string>());
        Assert.Equal(100, json["ai_percent"]!.GetValue<int>());
        var lines = json["lines"]!.AsArray();
        Assert.Equal([1, 3], lines.Select(l => l!["line"]!.GetValue<int>()));
        var l3 = lines[1]!;
        Assert.Equal("sess-000", l3["session"]!.GetValue<string>()[..8]);
        Assert.Equal(1, l3["prompt"]!.GetValue<int>());
        Assert.Equal("Add <retry> & backoff", l3["prompt_text"]!.GetValue<string>());
        Assert.Equal("Edit", l3["tool"]!.GetValue<string>());
        Assert.EndsWith("10:11:00.000Z", l3["ts"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("src/a.cs:57", "src/a.cs", 57)]
    [InlineData(@"C:\repo\a.cs", @"C:\repo\a.cs", null)]
    [InlineData(@"C:\repo\a.cs:3", @"C:\repo\a.cs", 3)]
    [InlineData("a.cs", "a.cs", null)]
    public void SplitLine(string input, string file, int? line) => Assert.Equal((file, line), Readers.SplitLine(input));

    [Fact]
    public void Report_Html_IsSelfContainedAndEscaped()
    {
        using var p = Recorded();
        var file = p.Path("r.html");
        Run(w => Readers.Report(p.Root, null, "html", file, w));
        var html = File.ReadAllText(file);
        Assert.Contains("Add &lt;retry&gt; &amp; backoff", html);
        Assert.DoesNotContain("Add <retry>", html);
        Assert.Contains("VERIFICATION STALE", html);
        Assert.Contains("CHAIN ✔ VERIFIED", html);
        Assert.Contains("failed-tool-call", html);
        Assert.Contains("chain head <code>sha256:", html);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Report_Markdown_ToStdout()
    {
        using var p = Recorded();
        var md = Run(w => Readers.Report(p.Root, "sess", "md", null, w));
        Assert.Contains("## Flight report: session `sess-000`", md);
        Assert.Contains("| Verification | **STALE**", md);
        Assert.Contains("| `src/Order.cs` | 2 | 8 | 0 | #1 |", md);
    }

    [Fact]
    public void Report_UnknownFormat_Throws()
    {
        using var p = Recorded();
        Assert.Throws<InvalidOperationException>(() => Readers.Report(p.Root, null, "pdf", null, TextWriter.Null));
    }

    [Fact]
    public void NoBlackbox_Throws()
    {
        using var p = new TempProject();
        Assert.Throws<InvalidOperationException>(() => Readers.Sessions(p.Root, TextWriter.Null, NoColor));
    }
}

public class InvestigateCommandTests
{
    [Fact]
    public void Investigate_ReportsFreshnessAtCommitTime()
    {
        using var p = new TempProject();
        Installer.Install(p.Root, false, false, null);
        void Git(params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = p.Root, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            psi.Environment["GIT_AUTHOR_DATE"] = psi.Environment["GIT_COMMITTER_DATE"] = "2026-11-24T10:20:00Z";
            using var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);
        }
        Git("init", "-q");
        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "t");
        Git("config", "core.autocrlf", "false");
        new SessionBuilder(p).At(5).Prompt("Add order").Edit("src/Order.cs", "class Order { }").At(10).Test()
            .At(30).Edit("src/Order.cs", "class Order { int later; }");
        File.WriteAllText(p.Path("src", "Order.cs"), "class Order { }\n");
        Git("add", "src");
        Git("commit", "-q", "-m", "Add order");

        using var w = new StringWriter();
        Assert.Equal(0, Readers.Investigate(p.Root, "HEAD", null, w));
        var md = w.ToString();
        Assert.Contains("| `src/Order.cs` | exact | `sess-000` |", md);
        Assert.Contains("Verification at commit time: **VERIFIED**", md);
        Assert.Contains("#1: Add order", md);
        Assert.Contains("5 passed", md);
    }
}
