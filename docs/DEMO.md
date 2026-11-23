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

- GIF: any screen recorder; crop to the terminal; 10 seconds; 1200 px wide; save as `docs/images/demo.gif`.
- Screenshot: open the HTML report at about 1100 px wide and capture the summary and the first prompt; save as `docs/images/report.png`.
- Before committing either file, check that no paths, user names or secrets are visible.
