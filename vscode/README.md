# agent-blackbox for VS Code

Marks the lines an AI coding agent wrote, using the recordings of [agent-blackbox](https://github.com/sayinbrahim/agent-blackbox) (`bbx`).

- An amber bar in the gutter (and the overview ruler) on every AI-written line that is unchanged since the agent wrote it.
- Hover a marked line to see the session, the prompt behind it, the tool and the time.
- The status bar shows the file's AI share, e.g. `AI 42%`.

It reads `.blackbox/` through `bbx blame --json`, so the attribution is the same as on the command line. Nothing leaves your machine.

## Requirements

- `bbx` 0.3.0 or later: `dotnet tool install -g AgentBlackbox`, or a binary from [GitHub Releases](https://github.com/sayinbrahim/agent-blackbox/releases).
- A project recorded with `bbx init`. The extension activates when the workspace has `.blackbox/config.json`.

## Settings

| Setting | Default | |
|---|---|---|
| `agentBlackbox.bbxPath` | `bbx` | Path to the bbx binary. |
| `agentBlackbox.enabled` | `true` | Show the markers. The **agent-blackbox: Toggle AI line markers** command flips it. |

Markers disappear while a file has unsaved changes, since its lines have moved, and come back when it is saved. A line counts as AI-written only when it is exactly what a recorded agent edit added. A line you changed afterwards counts as yours.

## Development

```sh
npm ci
npm test                 # unit tests
npm run package          # agent-blackbox-<version>.vsix
node out/test/runIntegration.js <workspace> <file with AI lines> <bbx>   # real VS Code (downloads it)
```
