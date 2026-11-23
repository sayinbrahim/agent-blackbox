# agent-blackbox

**A flight recorder for AI coding agents.**

`bbx` records what Claude Code does in your repository into an append-only, hash-chained log. Every prompt, tool call, file edit and command goes into `.blackbox/`. Later milestones turn that log into incident reports and line-level provenance ("which prompt wrote this line?").

> Status: Milestone 1 of the v0.1 plan. Recording, `init` and `verify` work. `timeline`, `report`, `blame` and `investigate` come next.

## Quick start

```
dotnet build -c Release
cd your-project
path/to/bbx init --local      # hooks point at this bbx binary (settings.local.json)
# or, with bbx on PATH:
bbx init                      # hooks go to .claude/settings.json, shared with the team
```

Then use Claude Code as usual. Check the log at any time:

```
bbx verify
```

## Commands

| Command | What it does |
|---|---|
| `bbx init [--local] [--private]` | Creates `.blackbox/` and merges hooks into `.claude/settings.json` (or `settings.local.json` with `--local`). Existing settings and hooks are kept; a `.bbx-backup` copy is written first. Running it twice changes nothing. |
| `bbx init --uninstall` | Removes bbx hooks from both settings files. Leaves `.blackbox/` alone. |
| `bbx hook <event>` | Called by Claude Code. Reads the hook payload from stdin and appends one record. Always exits 0, never prints. |
| `bbx verify` | Recomputes every session's hash chain. Exit code 1 if a record was edited, deleted or reordered. |

## What it records

One JSONL file per session in `.blackbox/sessions/`. Each line has `seq`, `ts`, `event` (raw hook name), `kind` (`PromptSubmitted`, `FileModified`, `TestCompleted`, ...), `actor`, `prompt_seq` (the prompt that led to it), event `data`, and `prev`/`hash` for the chain.

- File edits: repo-relative path, diff (capped at 16 KB), added/removed line counts, normalized hashes of added lines, SHA-256 of the file before and after. Never the full file.
- Commands: the command, exit code, the last 1 KB of output. Test and build commands are recognized (`dotnet test`, `npm test`, `pytest`, `cargo build`, ...).
- Prompts: full text by default; `record_prompts` can be `hash` or `off`.

## Privacy

Redaction runs before anything touches the disk: API keys, GitHub tokens, JWTs, private keys, passwords in connection strings and `.env` lines, credentials in URLs, plus your home directory and user name. Files matching `ignore_paths` (`.env*`, `**/secrets/**`, `*.pem`, `*.pfx`, `appsettings.*.json` by default) are recorded as "changed" with no diff or hashes. Transcript and scratchpad paths from Claude Code are dropped.

`.blackbox/` is committed by default so the team shares provenance. `bbx init --private` adds it to `.gitignore` instead. Redaction is pattern-based and can miss things; see [docs/SECURITY.md](docs/SECURITY.md).

## How it works

```
record[n].hash = SHA256( record[n].prev + "\n" + canonical_json(record[n] without hash) )
record[n].prev = record[n-1].hash        (record[0].prev = "sha256:GENESIS")
```

The chain is **tamper-evident, not tamper-proof**. It catches edits, deletions and reordering inside a log. Someone with write access can still regenerate the whole chain from scratch. Anchoring the chain head in git commits is planned for v0.2.

It also only sees what Claude Code passes to hooks. Commands you run in another terminal are not recorded.

## Performance

Each hook is a separate process, so start-up time matters. `bbx` is compiled with Native AOT. `scripts/bench-hook.ps1` measures it the way Claude Code calls it.

| Build | Median per hook (Windows) |
|---|---|
| JIT (framework-dependent) | 128 ms |
| Native AOT | measured in CI, see the `aot` job |

## Build

```
dotnet build
dotnet test
dotnet publish src/Blackbox.Cli -c Release -r win-x64
```

Native AOT publishing needs the platform C/C++ toolchain (on Windows: Visual Studio "Desktop development with C++").

## License

MIT
