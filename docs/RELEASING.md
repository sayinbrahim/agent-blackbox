# Releasing

Releases are cut by pushing a `v*` tag. `.github/workflows/release.yml` then:

1. Builds Native AOT binaries on the matching OS for `win-x64`, `linux-x64`, `osx-arm64` and `osx-x64`.
2. Packs one RID-specific tool package per platform (`AgentBlackbox.<rid>.<version>.nupkg`), a framework-dependent fallback (`AgentBlackbox.any.<version>.nupkg`) and the top-level package (`AgentBlackbox.<version>.nupkg`).
3. Installs the tool from those packages on Linux, records a hook event and runs `bbx verify` (smoke test).
4. Pushes the RID packages first and the top-level package last (installs fail if the top-level package arrives first), using nuget.org trusted publishing.
5. Creates the GitHub Release with the binaries and the `CHANGELOG.md` section for that version.

Pull requests that change packaging run steps 1–3 as a dry run with version `0.0.0-ci.<run>`.

## One-time setup on nuget.org (trusted publishing)

No API key is stored anywhere. GitHub Actions exchanges an OIDC token for a one-hour key.

1. Sign in to nuget.org, click your user name, then **Trusted Publishing**.
2. Add a policy:
   - Repository Owner: `sayinbrahim`
   - Repository: `agent-blackbox`
   - Workflow File: `release.yml` (file name only)
   - Environment: `release`
3. In the GitHub repository: **Settings → Secrets and variables → Actions → New repository secret**, name `NUGET_USER`, value: your nuget.org profile name (not your email).
4. In **Settings → Environments**, create an environment named `release`. Optionally add yourself as a required reviewer, so every publish waits for a click.

Packages published to nuget.org can be unlisted but never deleted. Treat every tag as irreversible.

## Cutting a release

1. Move the `[Unreleased]` entries in `CHANGELOG.md` to a new `## [x.y.z] - YYYY-MM-DD` section.
2. Commit, merge to `main`.
3. `git tag vx.y.z && git push origin vx.y.z`
4. Watch the run: `gh run watch`.
5. Check https://www.nuget.org/packages/AgentBlackbox and the GitHub Release page.

If the workflow fails after a tag, don't delete the tag. Fix the problem and release the next patch version.
