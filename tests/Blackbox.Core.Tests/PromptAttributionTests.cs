using System.Text.Json.Nodes;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

/// <summary>Which prompt an action belongs to: task notifications are not prompts, numbers are ordinal, resumes start fresh.</summary>
public class PromptAttributionTests
{
    private const string Notification = "<task-notification> <task-id>b1</task-id> <summary>Background command finished</summary> </task-notification>";

    private static SessionAnalysis Analyze(TempProject p) => new(SessionLog.LoadAll(p.Root).Single(), new BlackboxConfig());

    private static SessionBuilder Resume(SessionBuilder b) =>
        b.Add("SessionStart", "SessionStarted", null, new JsonObject { ["source"] = "resume", ["git_branch"] = "main" }, "system");

    // Old logs recorded notifications as PromptSubmitted; readers must still treat them as part of the real prompt.
    [Fact]
    public void TaskNotification_InOldLogs_IsNotAPrompt()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Prompt("Build the retry logic").Edit("src/a.cs", "int a = 1;").Prompt(Notification).Edit("src/b.cs", "int b = 2;");
        var a = Analyze(p);

        Assert.Equal(1, a.PromptCount);
        var g = Assert.Single(a.Groups);
        Assert.Equal("Build the retry logic", g.Prompt.O("data").S("prompt"));
        Assert.Equal(1, g.Number);
        Assert.Contains(g.Actions, r => SessionAnalysis.Describe(r) == "Background task notification: Background command finished");
        Assert.All(a.Files, f => Assert.Equal([1], f.PromptNumbers));

        var blame = Provenance.Blame(p.Root, "src/b.cs", SessionLog.LoadAll(p.Root));
        var line = Assert.Single(blame.Lines);
        Assert.Equal("Build the retry logic", line.Origin!.Prompt.O("data").S("prompt"));
    }

    [Fact]
    public void PromptNumbers_AreOrdinal_NotRecordSeq()
    {
        using var p = new TempProject();
        new SessionBuilder(p).Start().Prompt("first").Edit("src/a.cs", "int a = 1;").Test().Stop().Prompt("second").Edit("src/a.cs", "int a = 2;");
        var a = Analyze(p);

        Assert.Equal([1, 2], a.Groups.Where(g => g.Prompt is not null).Select(g => g.Number));
        var second = a.Groups.Single(g => g.Number == 2).Prompt!;
        Assert.True(second.Seq() > 2);
        Assert.Equal(2, a.Session.PromptNumber(second));
        Assert.Equal([1, 2], a.Files.Single().PromptNumbers);
    }

    [Fact]
    public void Resume_StartsItsOwnGroup_AndBelongsToNoPrompt()
    {
        using var p = new TempProject();
        var b = new SessionBuilder(p).Start().Prompt("first").Edit("src/a.cs", "int a = 1;").Stop();
        Resume(b).Prompt("second");
        var a = Analyze(p);

        Assert.Equal(4, a.Groups.Count);
        Assert.False(a.Groups[0].Resumed);
        var resumed = a.Groups[2];
        Assert.True(resumed.Resumed);
        Assert.Null(resumed.Prompt);
        var start = Assert.Single(resumed.Actions);
        Assert.Equal("Session resumed on main", SessionAnalysis.Describe(start));
        Assert.Null(a.Session.PromptOf(start));
        Assert.Equal("second", a.Groups[3].Prompt.O("data").S("prompt"));
    }

    [Fact]
    public void Recorder_WritesNotificationsAsTaskNotification_WithoutMovingPromptSeq()
    {
        using var p = new TempProject();
        var builder = new RecordBuilder(p.Root, new BlackboxConfig(), "test");
        var store = new SessionStore(p.Path(".blackbox"));
        var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        JsonObject Rec(string evt, JsonObject extra)
        {
            var input = new JsonObject { ["session_id"] = "s1", ["hook_event_name"] = evt, ["cwd"] = p.Root };
            foreach (var (k, v) in extra)
            {
                input[k] = v?.DeepClone();
            }
            return store.Append(builder.Build(input, evt, now), now);
        }

        var prompt = Rec("UserPromptSubmit", new() { ["prompt"] = "real prompt" });
        var note = Rec("UserPromptSubmit", new() { ["prompt"] = Notification });
        var after = Rec("Stop", new());
        var resume = Rec("SessionStart", new() { ["source"] = "resume" });

        Assert.Equal("TaskNotification", note.Kind());
        Assert.Equal("system", note.S("actor"));
        Assert.Equal(prompt.Seq(), note.I("prompt_seq"));
        Assert.Equal(prompt.Seq(), after.I("prompt_seq"));
        Assert.Null(resume.I("prompt_seq"));
    }
}
