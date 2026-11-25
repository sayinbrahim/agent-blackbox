# Releasing

Releases are cut by pushing a `v*` tag. `.github/workflows/release.yml` then:

1. Builds Native AOT binaries on the matching OS for `win-x64`, `linux-x64`, `osx-arm64` and `osx-x64`.
2. Packs one RID-specific tool package per platform (`AgentBlackbox.<rid>.<version>.nupkg`), a framework-dependent fallback (`AgentBlackbox.any.<version>.nupkg`) and the top-level package (`AgentBlackbox.<version>.nupkg`).
3. Packages the VS Code extension (`agent-blackbox-<version>.vsix`, version taken from the tag).
4. Installs the tool from those packages on Linux, records a hook event and runs `bbx verify` (smoke test).
5. Pushes the RID packages first and the top-level package last (installs fail if the top-level package arrives first), using nuget.org trusted publishing.
6. Creates the GitHub Release with the binaries, the `.vsix` and the `CHANGELOG.md` section for that version.
7. Publishes the extension to the VS Code Marketplace and Open VSX, each only when its token secret is set (see below).

Pull requests that change packaging run steps 1–4 as a dry run with version `0.0.0-ci.<run>`.

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

## One-time setup for the VS Code extension (optional)

Without these secrets the extension is still attached to every GitHub Release; the Marketplace and Open VSX steps print "skipping" and pass.

- **VS Code Marketplace:** create the publisher `sayinbrahim` at https://marketplace.visualstudio.com/manage (it must match `publisher` in `vscode/package.json`). Create an Azure DevOps personal access token with the **Marketplace → Manage** scope and organization **All accessible organizations**. Save it as the repository secret `VSCE_PAT`.
- **Open VSX** (VSCodium, Cursor and other editors): sign in at https://open-vsx.org with GitHub, sign the publisher agreement, create an access token, run `npx ovsx create-namespace sayinbrahim -p <token>` once, and save the token as the repository secret `OVSX_PAT`.

A published extension version can't be uploaded again, but it can be unpublished.

## Cutting a release

1. Move the `[Unreleased]` entries in `CHANGELOG.md` to a new `## [x.y.z] - YYYY-MM-DD` section.
2. Commit, merge to `main`.
3. `git tag vx.y.z && git push origin vx.y.z`
4. Watch the run: `gh run watch`.
5. Check https://www.nuget.org/packages/AgentBlackbox and the GitHub Release page.

If the workflow fails after a tag, don't delete the tag. Fix the problem and release the next patch version.
