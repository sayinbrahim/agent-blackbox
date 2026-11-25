# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [SemVer](https://semver.org/).

## [Unreleased]

## [0.2.1] - 2026-11-25

Ships everything listed under 0.2.0. The v0.2.0 tag was pushed during a GitHub outage and its release workflow never ran, so 0.2.0 was never published to NuGet or GitHub Releases. Use 0.2.1.

## [0.2.0] - 2026-11-25 (not published) - 2026-11-25

### Added
- `bbx check`, a CI gate for AI-written code. `--require-trace <regex>` fails when AI lines don't trace to a prompt with a requirement ID ("Untraced AI code: N files (no requirement link)"). `--require-tests` fails when a session changed source without running tests or touching a test file. `--base <ref>` limits the check to lines added since the merge base. Broken hash chains always fail the check.
- GitHub Action (`uses: sayinbrahim/agent-blackbox@v0.2.1`): downloads the native bbx binary for the runner, runs `bbx check` on what the pull request adds, and writes the result to the job summary.
- Chain-head anchoring: `bbx anchor --install-hook` adds a git hook that writes `Blackbox-Head: <session> <seq> sha256:<hash>` trailers for the session logs in each commit, and `bbx verify` fails when a log no longer matches an anchored head, which catches a log rewritten with a fresh, consistent chain.

## [0.1.2] - 2026-11-24

### Fixed
- Background task notifications are no longer counted as prompts. New records store them as `TaskNotification`; older logs are read the same way, so their actions and blamed lines go back to the prompt the user actually typed.
- Prompt numbers in `timeline`, `blame`, `report` and `investigate` are 1, 2, 3 in session order instead of record sequence numbers.
- A resumed session (`claude --continue`), compaction or clear shows as "Session resumed" in its own group instead of under the previous prompt, and belongs to no prompt.

## [0.1.1] - 2026-11-23

First release on nuget.org. The v0.1.0 tag exists, but its publish job failed before anything was uploaded, so 0.1.1 ships everything listed under 0.1.0.

### Fixed
- Release workflow: the publish job now finds the packages and binaries inside the downloaded artifact folders, and the dry run checks that every release file exists.

## [0.1.0] - 2026-11-23

### Added
- Distribution: `dotnet tool install -g AgentBlackbox` (Native AOT per platform, framework-dependent fallback), `dnx AgentBlackbox`, and binaries for win-x64, linux-x64, osx-arm64 and osx-x64 on GitHub Releases.
- `bbx sessions`, `bbx timeline`, `bbx blame`, `bbx report` (HTML and Markdown) and `bbx investigate --commit`.
- Verification freshness (`VERIFIED`, `STALE`, `FAILED`, `UNVERIFIED`) and `dotnet test` count parsing.
- Anomaly detection: failed tool calls, files edited 3+ times, deleted files, source changes without a test run.
- `bbx hook <event>`: records Claude Code hook events (`SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `Stop`, `SessionEnd`) into hash-chained JSONL. Always exits 0 and never prints.
- `bbx init` with `--local`, `--private` and `--uninstall`. Merges into existing Claude Code settings and backs them up first.
- `bbx verify`: detects edited, deleted and reordered records.
- Redaction of secrets, home directories and user names before writing; `ignore_paths`; `record_prompts` (`full`, `hash`, `off`).
- Test, build and delete command recognition.
- CI: build and test on Linux, Windows and macOS; Native AOT publish and hook benchmark.
- Solution skeleton: `Blackbox.Core`, `Blackbox.Cli` (`bbx`), `Blackbox.Report`.
