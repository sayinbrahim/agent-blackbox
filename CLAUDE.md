# CLAUDE.md — agent-blackbox

## Project
A flight recorder for AI coding agents. See agent-blackbox-PROJE-BRIEFv3.md for the full spec (source of truth).

## Communication
- Talk to Halil in Turkish. Code, comments, commits, docs: English.
- Stop after each milestone and wait for approval.
- Claude handles git (commits, branches, PRs, tags, push) and releases, following section 11A of the brief.
- Publishing to NuGet is irreversible: ask Halil for an explicit "yes" before pushing a release tag.
- Never handle the NuGet API key directly. Releases go through GitHub Actions (trusted publishing or the NUGET_API_KEY repo secret).
- Never force push, rewrite pushed history, push directly to main, or delete tags/releases.

## Non-negotiable rules
1. `bbx hook` must ALWAYS exit 0 and never write to stdout. The recorder must never break the agent.
2. Native AOT compatible code only: no reflection-based JSON, use JsonSerializerContext source generation. Keep AOT/trim warnings at zero.
3. Redact secrets BEFORE writing to disk.
4. `bbx init` merges into .claude/settings.json; never overwrite or delete existing user config. Back up first.
5. No new NuGet dependency without asking Halil.
6. Tests first for Core logic (canonical JSON, hash chain, redaction, provenance).
7. Small, focused commits with conventional commit messages (feat:, fix:, test:, docs:).
8. Don't overclaim security. The hash chain is tamper-evident, not tamper-proof.

## Commands
- Build: `dotnet build`
- Test: `dotnet test`
- AOT publish (Windows): `dotnet publish src/Blackbox.Cli -c Release -r win-x64`
