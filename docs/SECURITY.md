# Security

## What agent-blackbox does and does not promise

- The hash chain is **tamper-evident, not tamper-proof**. It detects edits, deletions and reordering inside a session log. It cannot stop someone from rewriting the entire chain from scratch. With `bbx anchor --install-hook`, each commit that includes session logs carries their chain heads as `Blackbox-Head` trailers, and `bbx verify` fails when a log no longer matches a commit that anchored it. That covers logs rewritten after a commit was shared. It does not cover rewritten git history, or logs that were never committed.
- A recorded session does not mean every action was captured. Only what Claude Code passes to hooks is visible. Commands run in a separate terminal are not seen.
- The pre-flight guard (`bbx init --guard`) is a seatbelt, not a security boundary. It matches the command line as written, so scripts, `bash -c`, variables and `cd` can get around it, and any error in the guard allows the call. It stops accidents, not an agent that is trying to get past it.
- Nothing is sent over the network. All data stays in `.blackbox/` in your repository.

## Secrets

Redaction runs on every string in a record before it is written to disk: tokens, keys, passwords, connection strings, private keys, URL credentials, `.env` lines, and the current user's home directory and user name (other `C:\Users\<name>`, `/home/<name>` and `/Users/<name>` paths become `~`). Claude Code's `transcript_path` and `scratchpad_dir` are dropped. Paths matching `ignore_paths` are recorded as "file changed" without a diff. Redaction is pattern-based and can miss things; review `.blackbox/` before committing it if you work with sensitive data, or use `bbx init --private`.

## Reporting a vulnerability

Open a private security advisory on GitHub.
