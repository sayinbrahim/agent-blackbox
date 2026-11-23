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
        Assert.Contains("#0: Add order", md);
        Assert.Contains("5 passed", md);
    }
}
