# Demo script

A 10-second GIF for the README and a screenshot of the HTML report. Record on a clean session so the report shows the branch and no setup noise.

## Setup (once)

```
dotnet tool install -g AgentBlackbox      # or put a release binary on PATH
mkdir orders-demo && cd orders-demo && git init
dotnet new classlib -n Orders -o src/Orders && dotnet new xunit -n Orders.Tests -o tests/Orders.Tests
bbx init
```

Start a new Claude Code session in `orders-demo` (recording starts at `SessionStart`).

## Scene (what to record)

1. Prompt Claude Code: *"Add an OrderService with retry logic for SubmitAsync, three attempts with backoff, and a test."* Let it edit and run `dotnet test`.
2. Prompt: *"Make the retry count 5."* Let it edit, and don't run the tests. The report will show STALE.
3. In a terminal, run each command for about 2 seconds:
   ```
   bbx timeline
   bbx blame src/Orders/OrderService.cs
   bbx blame src/Orders/OrderService.cs:12      # a line inside the retry loop
   bbx report && start bbx-report-*.html         # open on macOS, xdg-open on Linux
   ```

## Capture

The README images in `docs/images/` came from this script, run on 2026-11-23 with AgentBlackbox 0.1.1 from nuget.org:

- The two prompts ran headless (`claude -p "..." --model sonnet`, then `claude -p --continue "..."`) in a project outside the home directory, so no user paths show up.
- After the session, one line of `OrderService.cs` was edited by hand so `bbx blame` shows a human line.
- `report.png` is the HTML report, and `timeline.png` and `blame.png` are the plain (`NO_COLOR=1`) command output set in a terminal-styled page. All three were captured with headless Edge at 2x (`msedge --headless=new --screenshot --force-device-scale-factor=2`).
- `banner.png` is rendered from `banner.svg` the same way. The README uses the PNG because nuget.org doesn't show SVG images.

A GIF is still welcome: any screen recorder, cropped to the terminal, about 10 seconds and 1200 px wide, saved as `docs/images/demo.gif`.

Before committing any image, check that no paths, user names or secrets are visible.
