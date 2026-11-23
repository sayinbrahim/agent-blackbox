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

## Record format

One JSON object per line, written in canonical form. Fields: `v`, `seq`, `ts`, `session`, `event` (raw hook name), `kind` (semantic type, omitted for `PreToolUse` and tools without one), `actor` (`agent`, `human`, `system`), `tool`, `prompt_seq`, `data`, `raw_extra` (unknown payload fields, for forward compatibility), `prev`, `hash`.

Each record carries `prev` (hash of the previous record, `sha256:GENESIS` for the first) and `hash = SHA256(prev + "\n" + canonical_json(record without hash))`. Canonical JSON: keys sorted ordinally, no whitespace, UTF-8, numbers as written. Appends take `.blackbox/.lock` (exclusive open with retry) and read `seq`/`prev` from the last line inside the lock.

The chain is tamper-evident, not tamper-proof: anyone with write access can rewrite the whole chain.
