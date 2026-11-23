# Claude Code hooks: verified facts

Checked against https://code.claude.com/docs/en/hooks on 2026-11-23. Re-check when Claude Code changes.

## Config shape

```json
{
  "hooks": {
    "PostToolUse": [
      { "matcher": "*", "hooks": [ { "type": "command", "command": "bbx hook PostToolUse", "timeout": 5 } ] }
    ]
  }
}
```

- `timeout` is in seconds. Default for command hooks is 600 (30 on `UserPromptSubmit`).
- `SessionEnd` hooks share a 1.5 s budget by default. Hook work must stay well under that.
- `matcher` is ignored on events without matcher support (`UserPromptSubmit`, `Stop`).
- Hooks from `~/.claude/settings.json`, `.claude/settings.json` and `.claude/settings.local.json` are merged. Hooks run in parallel.

## Events bbx listens to

| Event | Key stdin fields |
|---|---|
| `SessionStart` | `source` (`startup`, `resume`, `clear`, `compact`, `fork`), `model` (optional) |
| `UserPromptSubmit` | `prompt` |
| `PreToolUse` | `tool_name`, `tool_input`, `tool_use_id` |
| `PostToolUse` | `tool_name`, `tool_input`, `tool_response`, `tool_use_id`, `duration_ms` (optional) |
| `PostToolUseFailure` | `tool_name`, `tool_input`, `tool_use_id`, `error`, `is_interrupt` |
| `Stop` | `stop_hook_active`, `last_assistant_message` |
| `SessionEnd` | `reason` (`clear`, `resume`, `logout`, `prompt_input_exit`, `other`) |

Common fields on every event: `session_id`, `prompt_id`, `transcript_path`, `cwd`, `hook_event_name`, and usually `permission_mode`. Unknown fields are kept under `raw_extra`.

`file_path` in `Write`/`Edit`/`Read` input is absolute; on Windows it uses backslashes. bbx stores repo-relative paths with forward slashes.

File-editing tools today: `Edit`, `Write`, `NotebookEdit`. `MultiEdit` is no longer listed. Shell tools: `Bash` and, on Windows, `PowerShell`.

## Exit codes and stdout

- Exit 0: success. Plain stdout is **added to Claude's context** on `UserPromptSubmit` and `SessionStart`. bbx never writes to stdout.
- Exit 2: blocking (on `PreToolUse` it blocks the tool, on `Stop` it keeps Claude going). bbx never uses it in record mode.
- Other non-zero codes: non-blocking, but Claude Code shows a "hook error" notice. bbx always exits 0.

## Windows

- Shell-form commands run in Git Bash, or PowerShell when Git Bash is missing.
- If `bbx` is not on PATH, every hook prints a "hook error" notice but the agent keeps working.

## Observed on Windows (2026-11-23)

Captured with a temporary hook that saved stdin (see `fixtures/hook-events/`):

- Hook changes in `.claude/settings.local.json` were picked up by a running session without a restart.
- `transcript_path` and `scratchpad_dir` contain the user's home directory (and so their name). bbx drops both.
- `cwd` follows Claude when it changes directory; `CLAUDE_PROJECT_DIR` stays at the project root. bbx uses `CLAUDE_PROJECT_DIR`, then `cwd`, and walks up to the nearest `.blackbox/`.
- `PostToolUse` for `Edit` has `tool_response.originalFile` and `structuredPatch` (hunks with `oldStart`, `oldLines`, `newStart`, `newLines`, `lines`). `Write` has `type` (`create`/`update`). `Read` returns the file content, which bbx does not store.
- A failing `Bash` call arrives as `PostToolUseFailure` with `error` = `"Exit code N\n<output>"` and no `tool_response`.
- Successful `Bash`: `tool_response` has `stdout`, `stderr`, `interrupted`, `isImage`. No exit code field.

## Why `bbx init` writes `bbx hook ...` and not a full path

`.claude/settings.json` is shared through git, so it must not contain a path from one person's machine. It gets `bbx hook <Event>` and relies on PATH. `bbx init --local` writes the absolute path of the running binary (with forward slashes, which work in both Git Bash and PowerShell) to `.claude/settings.local.json` and adds that file to `.gitignore`.
