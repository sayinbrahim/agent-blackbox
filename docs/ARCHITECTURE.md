# Architecture

```
Claude Code hook --stdin JSON--> bbx hook <event> --append + hash chain--> .blackbox/sessions/<session>.jsonl
                                                                                  |
                                        bbx verify | sessions | timeline | report | blame | investigate
```

## Projects

| Project | Responsibility |
|---|---|
| `Blackbox.Core` | Record model, canonical JSON, hash chain, storage and file lock, redaction, provenance. No external dependencies. |
| `Blackbox.Cli` | The `bbx` executable. Native AOT. |
| `Blackbox.Report` | HTML and Markdown report generation. Templates are embedded resources. |

## The golden rule

The recorder never brings the aircraft down. `bbx hook` always exits 0 and never writes to stdout. Errors go to `.blackbox/errors.log`.

The opt-in guard (`bbx guard`) is the only command that can stop a tool call: exit code 2 with the reason on stderr. It is a separate hook, so the recorder's behaviour does not change. It fails open: bad input, a bad rule, or a check slower than 2 seconds allows the call. A block is recorded as a `ToolBlocked` record (event `GuardBlocked`).

## Other agents

`AgentAdapter` turns Codex, Cursor and Copilot CLI hook payloads into Claude Code-shaped payloads before `RecordBuilder` sees them, so records, the chain, provenance and the guard have one code path. One payload can become several (a Codex `apply_patch` over three files gives three edit records). Fields an agent doesn't send are not invented: a shell command without an exit code gets `success: null` and is not counted as a passing test. `SessionStarted` records carry `data.agent`, and file edits keep the agent's own tool name in `data.agent_tool`.

## Record format

One JSON object per line, written in canonical form. Fields: `v`, `seq`, `ts`, `session`, `event` (raw hook name), `kind` (semantic type, omitted for `PreToolUse` and tools without one), `actor` (`agent`, `human`, `system`), `tool`, `prompt_seq`, `data`, `raw_extra` (unknown payload fields, for forward compatibility), `prev`, `hash`.

`prompt_seq` points to the latest user prompt. Background task notifications (Claude Code sends them as `UserPromptSubmit` wrapped in `<task-notification>`) are recorded as `TaskNotification` and keep the current `prompt_seq`; a `SessionStart` inside a session (resume, compact, clear) has none. Readers derive attribution from record order with the same rules, so logs written before 0.1.2, where notifications moved `prompt_seq`, read correctly without touching the chain. Reports number prompts 1, 2, 3 in session order; `seq` stays the record's position in the chain.

Each record carries `prev` (hash of the previous record, `sha256:GENESIS` for the first) and `hash = SHA256(prev + "\n" + canonical_json(record without hash))`. Canonical JSON: keys sorted ordinally, no whitespace, UTF-8, numbers as written. Appends take `.blackbox/.lock` (exclusive open with retry) and read `seq`/`prev` from the last line inside the lock.

The chain is tamper-evident, not tamper-proof: anyone with write access can rewrite the whole chain. Anchors narrow that: a `prepare-commit-msg` hook (`bbx anchor --install-hook`) adds `Blackbox-Head: <session> <seq> sha256:<hash>` for the head of each session log staged in the commit (the staged blob, not the working tree, since the agent keeps appending). `bbx verify` reads those trailers from `git log HEAD` and requires each anchored record to still exist with the same hash.

## Read side

`SessionLog` loads a session file and verifies its chain. `SessionAnalysis` derives everything the readers show from the records alone: prompt groups, file touches, anomalies and verification freshness. `Provenance.Blame` and `Investigator` add the current working tree and git (`git` is only invoked by `investigate`, never by the hook). `Blackbox.Report` renders the HTML (embedded template, inline CSS, no scripts) and Markdown reports.

Everything in a report is deterministic evidence from the log. Nothing is inferred by a model.
