# Hook event fixtures

Stdin payloads from Claude Code hooks, captured on Windows on 2026-11-23 and scrubbed (user paths replaced with `C:\Users\dev`, project with `C:\repo`).

- Captured: `PreToolUse.edit`, `PostToolUse.write-create`, `PostToolUse.edit`, `PostToolUse.read`, `PostToolUseFailure.bash`.
- Hand-written from the documented schema, using the captured common fields: `SessionStart`, `UserPromptSubmit` (contains a fake token to test redaction), `Stop`, `SessionEnd`, `PostToolUse.bash-test` (has an unknown field to test `raw_extra`).
