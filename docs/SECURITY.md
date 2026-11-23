# Security

## What agent-blackbox does and does not promise

- The hash chain is **tamper-evident, not tamper-proof**. It detects edits, deletions and reordering inside a session log. It cannot stop someone from rewriting the entire chain from scratch.
- A recorded session does not mean every action was captured. Only what Claude Code passes to hooks is visible. Commands run in a separate terminal are not seen.
- Nothing is sent over the network. All data stays in `.blackbox/` in your repository.

## Secrets

Redaction runs before anything is written to disk (tokens, keys, passwords, connection strings, private keys). Paths matching `ignore_paths` are recorded as "file changed" without a diff. Redaction is pattern-based and can miss things; review `.blackbox/` before committing it if you work with sensitive data, or use `bbx init --private`.

## Reporting a vulnerability

Open a private security advisory on GitHub.
