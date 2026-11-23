# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [SemVer](https://semver.org/).

## [Unreleased]

### Added
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
