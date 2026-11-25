using System.Text.Json.Nodes;
using Blackbox.Core;
using Blackbox.Tests.Shared;

namespace Blackbox.Core.Tests;

/// <summary>Payload shapes follow each agent's published hook docs (2026-11). None of these agents ran in this test suite.</summary>
public class AdapterTests
{
    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    private static List<JsonObject> N(string agent, string json, string evt = "") => [.. AgentAdapter.Normalize(agent, J(json), evt)];

    [Fact]
    public void ClaudeCode_PassesThrough()
    {
        var input = J("""{ "session_id": "s", "hook_event_name": "Stop" }""");
        Assert.Same(input, Assert.Single(AgentAdapter.Normalize("claude-code", input, "Stop")));
    }

    [Fact]
    public void UnknownAgent_Throws() => Assert.Throws<InvalidDataException>(() => N("vim", "{}"));

    // ---- Codex: Claude-shaped events; edits come as apply_patch, shell output as text.

    private const string Patch = """
        *** Begin Patch
        *** Add File: src/New.cs
        +public class New
        +{
        +}
        *** Update File: src/Order.cs
        @@ public class Order
         {
        -    int retries = 3;
        +    int retries = 5;
        +    int delay = 100;
         }
        *** Delete File: src/Old.cs
        *** End Patch
        """;

    [Fact]
    public void Codex_ApplyPatch_BecomesOneEditPerFile()
    {
        var input = new JsonObject
        {
            ["session_id"] = "thr_1", ["cwd"] = "/repo", ["hook_event_name"] = "PostToolUse", ["turn_id"] = "t1",
            ["tool_name"] = "apply_patch", ["tool_use_id"] = "call_1",
            ["tool_input"] = new JsonObject { ["command"] = Patch }, ["tool_response"] = "Success. Updated the following files: ...",
        };
        var outs = AgentAdapter.Normalize("codex", input, "PostToolUse");

        Assert.Equal(["Write", "Edit", "Delete"], outs.Select(o => o.S("tool_name")));
        Assert.All(outs, o => Assert.Equal("apply_patch", o.S("agent_tool")));
        Assert.All(outs, o => Assert.Equal("call_1", o.S("tool_use_id")));

        var add = outs[0].O("tool_input");
        Assert.Equal("src/New.cs", add.S("file_path"));
        Assert.Equal("public class New\n{\n}\n", add.S("content"));
        Assert.Equal("create", outs[0].O("tool_response").S("type"));

        var edit = outs[1].O("tool_input");
        Assert.Equal("    int retries = 3;\n", edit.S("old_string"));
        Assert.Equal("    int retries = 5;\n    int delay = 100;\n", edit.S("new_string")); // context lines are not counted as written
        Assert.Equal("src/Old.cs", outs[2].O("tool_input").S("file_path"));
    }

    [Fact]
    public void Codex_ApplyPatch_MoveTo_UsesTheNewPath()
    {
        var patch = "*** Begin Patch\n*** Update File: a.cs\n*** Move to: b.cs\n@@\n-x\n+y\n*** End Patch";
        var input = new JsonObject { ["hook_event_name"] = "PreToolUse", ["tool_name"] = "apply_patch", ["tool_input"] = new JsonObject { ["command"] = patch } };
        Assert.Equal("b.cs", Assert.Single(AgentAdapter.Normalize("codex", input, "")).O("tool_input").S("file_path"));
    }

    [Theory]
    [InlineData("Exit code: 0\nWall time: 1.2 seconds\nOutput:\nPassed!", "PostToolUse", 0)]
    [InlineData("Exit code: 1\nWall time: 3 seconds\nOutput:\nFailed!  - Failed: 1", "PostToolUseFailure", 1)]
    public void Codex_Bash_ExitCodeComesFromTheOutput(string output, string evt, int code)
    {
        var o = Assert.Single(N("codex", $$"""
            { "session_id": "thr_1", "hook_event_name": "PostToolUse", "tool_name": "Bash",
              "tool_input": { "command": "dotnet test" }, "tool_response": {{JsonValue.Create(output).ToJsonString()}} }
            """));
        Assert.Equal(evt, o.S("hook_event_name"));
        Assert.Contains(output, o.O("tool_response").S("stdout"));
        if (code != 0)
        {
            Assert.StartsWith($"Exit code {code}", o.S("error"));
        }
        Assert.Null(o["bbx_exit_unknown"]);
    }

    [Fact]
    public void Codex_Bash_WithoutExitCode_IsMarkedUnknown()
    {
        var o = Assert.Single(N("codex", """{ "hook_event_name": "PostToolUse", "tool_name": "Bash", "tool_input": { "command": "ls" }, "tool_response": "a\nb" }"""));
        Assert.Equal("PostToolUse", o.S("hook_event_name"));
        Assert.True(o["bbx_exit_unknown"]!.GetValue<bool>());
    }

    // ---- Cursor: its own event names; edits arrive as afterFileEdit.

    [Fact]
    public void Cursor_Events_MapToClaudeShapes()
    {
        const string common = """ "conversation_id": "conv-1", "generation_id": "g1", "model": "m", "cursor_version": "2.0", "workspace_roots": ["/repo"] """;

        var start = Assert.Single(N("cursor", $$"""{ {{common}}, "hook_event_name": "sessionStart", "session_id": "conv-1", "is_background_agent": false }"""));
        Assert.Equal(("SessionStart", "conv-1", "/repo"), (start.S("hook_event_name"), start.S("session_id"), start.S("cwd")));

        var prompt = Assert.Single(N("cursor", $$"""{ {{common}}, "hook_event_name": "beforeSubmitPrompt", "prompt": "Add retries (REQ-7)", "attachments": [] }"""));
        Assert.Equal(("UserPromptSubmit", "Add retries (REQ-7)"), (prompt.S("hook_event_name"), prompt.S("prompt")));

        var edits = N("cursor", $$"""
            { {{common}}, "hook_event_name": "afterFileEdit", "file_path": "/repo/src/Order.cs",
              "edits": [ { "old_string": "int retries = 3;", "new_string": "int retries = 5;" }, { "old_string": "", "new_string": "int delay = 100;" } ] }
            """);
        Assert.Equal(2, edits.Count);
        Assert.All(edits, e => Assert.Equal(("PostToolUse", "Edit", "/repo/src/Order.cs", "conv-1"), (e.S("hook_event_name"), e.S("tool_name"), e.O("tool_input").S("file_path"), e.S("session_id"))));
        Assert.Equal("int delay = 100;", edits[1].O("tool_input").S("new_string"));

        var shell = Assert.Single(N("cursor", $$"""{ {{common}}, "hook_event_name": "afterShellExecution", "command": "dotnet test", "output": "Passed!", "duration": 1200, "sandbox": false }"""));
        Assert.Equal(("PostToolUse", "Bash", "dotnet test", "Passed!"), (shell.S("hook_event_name"), shell.S("tool_name"), shell.O("tool_input").S("command"), shell.O("tool_response").S("stdout")));
        Assert.True(shell["bbx_exit_unknown"]!.GetValue<bool>()); // Cursor does not pass the exit code

        var pre = Assert.Single(N("cursor", $$"""{ {{common}}, "hook_event_name": "beforeShellExecution", "command": "git push --force", "cwd": "/repo/src", "sandbox": false }"""));
        Assert.Equal(("PreToolUse", "Bash", "/repo/src"), (pre.S("hook_event_name"), pre.S("tool_name"), pre.S("cwd")));

        var del = Assert.Single(N("cursor", $$"""{ {{common}}, "hook_event_name": "preToolUse", "tool_name": "Delete", "tool_input": { "path": "src/Migrations/x.cs" }, "tool_use_id": "u1", "cwd": "/repo" }"""));
        Assert.Equal(("Delete", "src/Migrations/x.cs"), (del.S("tool_name"), del.O("tool_input").S("file_path")));

        var stop = Assert.Single(N("cursor", $$"""{ {{common}}, "hook_event_name": "stop", "status": "completed", "loop_count": 0 }"""));
        Assert.Equal("Stop", stop.S("hook_event_name"));
        var end = Assert.Single(N("cursor", $$"""{ {{common}}, "hook_event_name": "sessionEnd", "session_id": "conv-1", "reason": "completed", "duration_ms": 5 }"""));
        Assert.Equal(("SessionEnd", "completed"), (end.S("hook_event_name"), end.S("reason")));
    }

    // ---- Copilot CLI: PascalCase hook names give snake_case fields; camelCase is accepted too.

    [Fact]
    public void Copilot_PascalCase_MapsToolsAndArguments()
    {
        var edit = Assert.Single(N("copilot", """
            { "hook_event_name": "PostToolUse", "session_id": "cp-1", "timestamp": "2026-11-25T10:00:00Z", "cwd": "/repo",
              "tool_name": "edit", "tool_input": { "path": "/repo/src/Order.cs", "old_str": "a", "new_str": "b" },
              "tool_result": { "result_type": "success", "text_result_for_llm": "ok" } }
            """));
        Assert.Equal(("Edit", "/repo/src/Order.cs", "a", "b"), (edit.S("tool_name"), edit.O("tool_input").S("file_path"), edit.O("tool_input").S("old_string"), edit.O("tool_input").S("new_string")));

        var create = Assert.Single(N("copilot", """
            { "hook_event_name": "PostToolUse", "session_id": "cp-1", "tool_name": "create", "tool_input": "{\"path\": \"src/New.cs\", \"file_text\": \"x\\ny\\n\"}",
              "tool_result": { "result_type": "success", "text_result_for_llm": "created" } }
            """));
        Assert.Equal(("Write", "x\ny\n", "create"), (create.S("tool_name"), create.O("tool_input").S("content"), create.O("tool_response").S("type")));
    }

    [Fact]
    public void Copilot_CamelCase_AndShellResults()
    {
        var ok = Assert.Single(N("copilot", """
            { "sessionId": "cp-1", "timestamp": 1735689600000, "cwd": "/repo", "toolName": "bash", "toolArgs": { "command": "dotnet test" },
              "toolResult": { "resultType": "success", "textResultForLlm": "Passed!\n<exited with exit code 0>" } }
            """, "postToolUse"));
        Assert.Equal(("PostToolUse", "cp-1", "Bash"), (ok.S("hook_event_name"), ok.S("session_id"), ok.S("tool_name")));
        Assert.Null(ok["bbx_exit_unknown"]);

        var failed = Assert.Single(N("copilot", """
            { "hook_event_name": "PostToolUse", "session_id": "cp-1", "tool_name": "bash", "tool_input": { "command": "dotnet test" },
              "tool_result": { "result_type": "failure", "text_result_for_llm": "Failed!\n<exited with exit code 1>" } }
            """));
        Assert.Equal("PostToolUseFailure", failed.S("hook_event_name"));
        Assert.StartsWith("Exit code 1", failed.S("error"));

        var prompt = Assert.Single(N("copilot", """{ "sessionId": "cp-1", "timestamp": 1, "cwd": "/repo", "prompt": "hi" }""", "userPromptSubmitted"));
        Assert.Equal(("UserPromptSubmit", "hi"), (prompt.S("hook_event_name"), prompt.S("prompt")));
        var stop = Assert.Single(N("copilot", """{ "sessionId": "cp-1", "timestamp": 1, "cwd": "/repo" }""", "agentStop"));
        Assert.Equal("Stop", stop.S("hook_event_name"));
    }

    // ---- End to end through the recorder and the guard.

    [Fact]
    public void Recorder_RecordsCodexPatch_ForBlame()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".blackbox", "sessions"));
        Directory.CreateDirectory(p.Path("src"));
        File.WriteAllText(p.Path("src", "New.cs"), "public class New\n{\n}\n");

        var start = new JsonObject { ["session_id"] = "thr_1", ["cwd"] = p.Root, ["hook_event_name"] = "SessionStart", ["source"] = "startup" };
        HookRecorder.Record("SessionStart", start.ToJsonString(), p.Root, "x", DateTimeOffset.UnixEpoch, "codex");
        var post = new JsonObject
        {
            ["session_id"] = "thr_1", ["cwd"] = p.Root, ["hook_event_name"] = "PostToolUse", ["tool_name"] = "apply_patch",
            ["tool_input"] = new JsonObject { ["command"] = "*** Begin Patch\n*** Add File: src/New.cs\n+public class New\n+{\n+}\n*** End Patch" },
            ["tool_response"] = "Success.",
        };
        HookRecorder.Record("PostToolUse", post.ToJsonString(), p.Root, "x", DateTimeOffset.UnixEpoch.AddSeconds(1), "codex");

        var session = SessionLog.LoadAll(p.Root).Single();
        Assert.True(session.Verify.Ok);
        Assert.Equal("codex", session.Records[0].O("data").S("agent"));
        var created = session.Records[1];
        Assert.Equal(("FileCreated", "apply_patch"), (created.Kind(), created.O("data").S("agent_tool")));
        Assert.Equal(100, Provenance.Blame(p.Root, "src/New.cs", [session]).AiPercent);
    }

    [Fact]
    public void Recorder_UnknownExitCode_IsNotCountedAsPassingTests()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".blackbox", "sessions"));
        var shell = new JsonObject
        {
            ["conversation_id"] = "conv-1", ["workspace_roots"] = new JsonArray(p.Root), ["hook_event_name"] = "afterShellExecution",
            ["command"] = "dotnet test", ["output"] = "some output", ["duration"] = 10,
        };
        HookRecorder.Record("afterShellExecution", shell.ToJsonString(), p.Root, "x", DateTimeOffset.UnixEpoch, "cursor");

        var r = SessionLog.LoadAll(p.Root).Single().Records.Single();
        Assert.Equal("TestCompleted", r.Kind());
        Assert.Null(r.O("data")!["success"]);
        Assert.Null(r.O("data")!["exit_code"]);
        var f = SessionAnalysis.ComputeFreshness([r], new BlackboxConfig());
        Assert.Equal(VerificationState.Unverified, f.State);
        Assert.Contains("result was not recorded", f.Message);
    }

    [Fact]
    public void Guard_BlocksCodexPatchThatDeletesAMigration()
    {
        using var p = new TempProject();
        Directory.CreateDirectory(p.Path(".blackbox", "sessions"));
        var pre = new JsonObject
        {
            ["session_id"] = "thr_1", ["cwd"] = p.Root, ["hook_event_name"] = "PreToolUse", ["tool_name"] = "apply_patch",
            ["tool_input"] = new JsonObject { ["command"] = "*** Begin Patch\n*** Update File: src/A.cs\n@@\n-x\n+y\n*** Delete File: src/Data/Migrations/1_Init.cs\n*** End Patch" },
        };
        var outcome = Guard.Check(pre.ToJsonString(), p.Root, "codex")!;
        Assert.Equal(("delete-migrations", "src/Data/Migrations/1_Init.cs"), (outcome.Block.Rule.Id, outcome.Block.Target));
    }
}
