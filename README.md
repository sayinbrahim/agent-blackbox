# agent-blackbox

**A flight recorder for AI coding agents.**

`bbx` records what Claude Code does in your repository into a hash-chained log you can verify. When something breaks, it writes the incident report. It can also tell you, line by line, whether code came from the agent, from which session and from which prompt.

<!-- Demo GIF goes here once recorded: see docs/DEMO.md -->

## Why

I used to write avionics software under DO-178C. In that world no line of code goes without an owner: each one traces to a requirement, a test and a record. When an aircraft goes down, the flight recorder tells you what happened, second by second.

Agents now write a large share of our code, and none of it leaves a record. Nobody can say why the agent touched a file, which prompt caused a regression, or which lines a person actually wrote. `bbx` is my attempt to bring the flight recorder over. Aviation is where the idea comes from, nothing more: this tool makes no certification claims.

## Quick start

```
dotnet tool install -g AgentBlackbox    # needs the .NET 10 SDK; or grab a binary from Releases
bbx init                                # in your project: creates .blackbox/ and adds Claude Code hooks
```

Then use Claude Code as usual. When you want to look:

```
bbx timeline          # what happened in the latest session
bbx report            # HTML incident report
bbx blame src/Orders/OrderService.cs:57
```

With the .NET 10 SDK you can also run it without installing: `dnx AgentBlackbox -- timeline`.

## What it records

One JSONL file per session in `.blackbox/sessions/`. Each line holds the hook event, a semantic `kind` (`PromptSubmitted`, `FileModified`, `TestCompleted`, ...), the actor, the prompt that led to it, and the event data:

- For a file edit it keeps the repo-relative path, the diff (up to 16 KB), how many lines were added and removed, normalized hashes of the added lines, and SHA-256 hashes of the file before and after. It never stores the whole file.
- For a command it keeps the command, its exit code and the last 1 KB of output, and it notes when the command was a test run or a build.
- For a prompt it keeps the full text by default. Set `record_prompts` to `hash` or `off` if you'd rather it didn't.

### Privacy

Nothing leaves your machine. Before `bbx` writes a record, it redacts API keys, GitHub tokens, JWTs, private keys, passwords in connection strings and `.env` lines, credentials in URLs, and your own home directory and user name. Files that match `ignore_paths` (`.env*`, `**/secrets/**`, `*.pem`, `*.pfx`, `appsettings.*.json` by default) are recorded as "changed", with no diff and no hashes.

`.blackbox/` is committed by default, so the team shares the provenance. Use `bbx init --private` to keep it out of git. Redaction works by pattern matching and can miss things. See [docs/SECURITY.md](docs/SECURITY.md).

## Commands

| Command | What it does |
|---|---|
| `bbx init [--local] [--private]` | Creates `.blackbox/` and merges hooks into `.claude/settings.json` (`--local`: `settings.local.json` with this binary's absolute path). Keeps existing settings, backs them up first, and does nothing the second time. |
| `bbx init --uninstall` | Removes the bbx hooks. Leaves `.blackbox/` alone. |
| `bbx verify` | Recomputes every hash chain. Exit code 1 if a record was edited, deleted or reordered. |
| `bbx sessions` | Lists sessions with duration, prompts, tool calls, files, chain status and verification state. |
| `bbx timeline [session]` | Prompts and the actions they led to, with failures and warnings. Honors `NO_COLOR`. |
| `bbx blame <file>[:line]` | AI/human split for a file, or the session, prompt and tool behind a single line. |
| `bbx report [session] [--format html\|md] [-o file]` | Single-file HTML incident report (no scripts, works offline), or Markdown for a PR description. |
| `bbx investigate --commit <sha>` | The recorded sessions behind a commit, with the prompts, the build and test evidence at commit time, and any changed files that no session explains. |
| `bbx hook <event>` | Called by Claude Code. Always exits 0 and never prints, so the recorder can't break the agent. |

`session` is an id prefix and defaults to the latest session.

### Verification freshness

`bbx` notices when the agent runs tests or builds, and tracks whether source files changed afterwards:

| State | Meaning |
|---|---|
| `VERIFIED` | Tests passed after the last source change. The tests passed. That doesn't mean the code is correct. |
| `STALE` | Source changed after the last passing test run. |
| `FAILED` | The last test run failed. |
| `UNVERIFIED` | No test run recorded. |

Only commands the agent runs through its own tools are visible. Tests you run in another terminal are not.

## How it works

```
record[n].hash = SHA256( record[n].prev + "\n" + canonical_json(record[n] without hash) )
record[n].prev = record[n-1].hash            record[0].prev = "sha256:GENESIS"
```

The chain is **tamper-evident, not tamper-proof**. It catches a record that was edited, deleted or moved. Anyone with write access can still regenerate the whole chain. Anchoring the chain head in git commits is planned for v0.2.

`bbx blame` matches whitespace-normalized line hashes from recorded agent edits of the same file. A line the agent wrote and nobody changed is found even if it moved within the file. A line edited afterwards counts as human. Files the agent never touched while recording count as human or unrecorded.

## Performance

Claude Code starts a new process for every hook, so startup time is what counts. `bbx` is compiled with Native AOT and ships as a single 5 MB executable. Time per hook call, from starting the process to its exit, with the payload piped in:

| Machine | Median | p95 |
|---|---|---|
| Windows 10 desktop (author's machine), 100 runs | 29.5 ms | 32–36 ms |
| GitHub-hosted Linux runner, 50 runs | 7.8 ms | 9.7 ms |
| GitHub-hosted Windows runner, 50 runs | 24 ms | 26.7 ms |
| GitHub-hosted macOS runner, 50 runs | 51 ms | 90 ms |

The same build without AOT takes 181 ms on the Windows desktop. macOS runners are slow to start processes, and their numbers vary from run to run. Measure your own machine with `pwsh scripts/bench-hook.ps1 -Bbx <path-to-bbx>`.

## It recorded its own creation

`bbx` was installed in this repository as soon as recording worked (Milestone 1), and everything after that is in [.blackbox/](.blackbox/). The first recorded session covers the rest of Milestone 1 and Milestone 2: 8 prompts, 140 tool calls, 25 files changed, 297 chained records. Its [flight report](docs/flight-reports/c30ed484.md) ([HTML](docs/flight-reports/c30ed484.html)) shows a STALE warning: the last source change came after the last test run. [`investigate-c6d0b85.md`](docs/flight-reports/investigate-c6d0b85.md) traces a real commit. Two files match recorded edits exactly. The third matches none, because it was edited with `sed` rather than through the agent's edit tool.

## Roadmap

v0.2 adds `bbx check` for CI (fail when AI-changed code has no requirement ID in its prompt, or no test alongside it), a GitHub Action, and anchoring of the chain head in git commit trailers.

v0.3 is about prevention: an opt-in pre-flight checklist that can stop dangerous actions such as `git push --force` before they run. It also adds adapters for agents other than Claude Code and a VS Code extension that marks AI-written lines in the gutter.

## Build from source

```
dotnet build
dotnet test
dotnet publish src/Blackbox.Cli -c Release -r win-x64
```

Native AOT needs the platform C/C++ toolchain (on Windows: Visual Studio Build Tools with "Desktop development with C++"). Release steps are in [docs/RELEASING.md](docs/RELEASING.md).

## Author

Halil İbrahim Sayın: Senior .NET developer, formerly an avionics engineer working under DO-178C. Also wrote [dotnet-context-mcp](https://github.com/sayinbrahim/dotnet-context-mcp) and [ef-migration-safety](https://github.com/sayinbrahim/ef-migration-safety).

## License

MIT
