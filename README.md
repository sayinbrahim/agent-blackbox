# agent-blackbox

**A flight recorder for AI coding agents.**

`bbx` records what Claude Code does in your repository into an append-only, hash-chained log. When something breaks, it produces an incident report. It can tell you which lines were written by the agent, in which session, and from which prompt.

> Status: early development (Milestone 0). Nothing works yet beyond the skeleton.

## Build

```
dotnet build
dotnet test
dotnet publish src/Blackbox.Cli -c Release -r win-x64
```

Native AOT publishing needs the platform C/C++ toolchain (on Windows: Visual Studio "Desktop development with C++").

## License

MIT
