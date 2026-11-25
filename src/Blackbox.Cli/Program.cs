using System.CommandLine;
using System.Reflection;
using System.Text;
using Blackbox.Core;

namespace Blackbox.Cli;

public static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static int Main(string[] args)
    {
        // Hot path: hooks run on every tool call, so skip command-line parsing entirely.
        if (args.Length > 0 && args[0] == "hook")
        {
            using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            return RunHook(args.Length > 1 ? args[1] : "", stdin, Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"));
        }
        Console.OutputEncoding = new UTF8Encoding(false);
        return BuildCommand().Parse(args).Invoke();
    }

    /// <summary>Runs a read command, turning expected failures (no .blackbox, unknown session, git errors) into a message and exit code 1.</summary>
    private static int Guard(Func<int> run)
    {
        try
        {
            return run();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>The recorder never brings the aircraft down: always exit 0, never write to stdout or stderr.</summary>
    public static int RunHook(string eventName, TextReader stdin, string? projectDir)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var work = Task.Run(() =>
            {
                try
                {
                    HookRecorder.Record(eventName, stdin.ReadToEnd(), projectDir, Version, now);
                }
                catch (Exception ex)
                {
                    var root = (ex as RecorderException)?.Root ?? HookRecorder.FindRoot(projectDir) ?? HookRecorder.FindRoot(Environment.CurrentDirectory);
                    if (root is not null)
                    {
                        HookRecorder.LogError(root, ex.InnerException ?? ex, now);
                    }
                }
            });
            work.Wait(TimeSpan.FromSeconds(2)); // hard ceiling; a slow disk must not stall the agent
        }
        catch
        {
            // Swallow everything, including failures while logging.
        }
        return 0;
    }

    private static RootCommand BuildCommand()
    {
        var root = new RootCommand("bbx - a flight recorder for AI coding agents.");

        var uninstall = new Option<bool>("--uninstall") { Description = "Remove bbx hooks from .claude/settings.json and .claude/settings.local.json." };
        var local = new Option<bool>("--local") { Description = "Write hooks to .claude/settings.local.json with the absolute path of this bbx binary." };
        var @private = new Option<bool>("--private") { Description = "Add .blackbox/ to .gitignore so recordings stay on this machine." };
        var init = new Command("init", "Set up .blackbox/ and install Claude Code hooks in this project.") { uninstall, local, @private };
        init.SetAction(r =>
        {
            var dir = Environment.CurrentDirectory;
            var messages = r.GetValue(uninstall)
                ? Installer.Uninstall(dir)
                : Installer.Install(dir, r.GetValue(local), r.GetValue(@private), Environment.ProcessPath);
            foreach (var m in messages)
            {
                Console.WriteLine(m);
            }
            return 0;
        });
        root.Subcommands.Add(init);

        var verify = new Command("verify", "Recompute the hash chain of every session and report tampering. Exit code 1 if any chain is broken.");
        verify.SetAction(_ => Verify(Environment.CurrentDirectory));
        root.Subcommands.Add(verify);

        var sessions = new Command("sessions", "List recorded sessions.");
        sessions.SetAction(_ => Guard(() => Readers.Sessions(Environment.CurrentDirectory, Console.Out, Ansi.Auto())));
        root.Subcommands.Add(sessions);

        Argument<string?> SessionArg() => new("session") { Description = "Session id or prefix. Default: latest.", Arity = ArgumentArity.ZeroOrOne };

        var timelineSession = SessionArg();
        var timeline = new Command("timeline", "Show a session as prompts and the actions they led to. Honors NO_COLOR.") { timelineSession };
        timeline.SetAction(r => Guard(() => Readers.Timeline(Environment.CurrentDirectory, r.GetValue(timelineSession), Console.Out, Ansi.Auto())));
        root.Subcommands.Add(timeline);

        var blameTarget = new Argument<string>("file") { Description = "File, optionally with :line (e.g. src/Orders/OrderService.cs:57)." };
        var blame = new Command("blame", "Show which lines were written by the agent, in which session and from which prompt.") { blameTarget };
        blame.SetAction(r => Guard(() => Readers.Blame(Environment.CurrentDirectory, r.GetValue(blameTarget)!, Console.Out, Ansi.Auto())));
        root.Subcommands.Add(blame);

        var reportSession = SessionArg();
        var format = new Option<string>("--format") { Description = "html (default) or md.", DefaultValueFactory = _ => "html" };
        var output = new Option<string?>("--output", "-o") { Description = "Output file. Default: bbx-report-<session>.html, or stdout for md." };
        var report = new Command("report", "Write an incident report for a session.") { reportSession, format, output };
        report.SetAction(r => Guard(() => Readers.Report(Environment.CurrentDirectory, r.GetValue(reportSession), r.GetValue(format)!, r.GetValue(output), Console.Out)));
        root.Subcommands.Add(report);

        var commit = new Option<string>("--commit") { Description = "Commit to investigate (sha, tag or ref).", Required = true };
        var invOutput = new Option<string?>("--output", "-o") { Description = "Output file. Default: stdout (Markdown)." };
        var investigate = new Command("investigate", "Find the recorded sessions behind a commit and report the evidence.") { commit, invOutput };
        investigate.SetAction(r => Guard(() => Readers.Investigate(Environment.CurrentDirectory, r.GetValue(commit)!, r.GetValue(invOutput), Console.Out)));
        root.Subcommands.Add(investigate);

        var requireTrace = new Option<string?>("--require-trace") { Description = "Regex for requirement IDs (e.g. \"REQ-\\d+\"). Every AI line must come from a session whose prompts, up to that edit, contain one." };
        var requireTests = new Option<bool>("--require-tests") { Description = "Every session that changed source with AI code must have run tests or changed a test file." };
        var baseRef = new Option<string?>("--base") { Description = "Only check lines added since the merge base with this ref (e.g. origin/main). Needs full git history." };
        var check = new Command("check", "CI gate: check AI-written code against traceability and test rules. Exit code 1 on failure.") { requireTrace, requireTests, baseRef };
        check.SetAction(r => Guard(() => Readers.Check(Environment.CurrentDirectory, r.GetValue(requireTrace), r.GetValue(requireTests), r.GetValue(baseRef), Console.Out, Ansi.Auto())));
        root.Subcommands.Add(check);

        var hook = new Command("hook","Record one Claude Code hook event from stdin (called by Claude Code).") { new Argument<string>("event") };
        root.Subcommands.Add(hook); // listed for --help; handled by the fast path in Main

        return root;
    }

    public static int Verify(string dir)
    {
        var root = HookRecorder.FindRoot(dir);
        if (root is null)
        {
            Console.Error.WriteLine("No .blackbox/ found. Run 'bbx init' first.");
            return 1;
        }
        var store = new SessionStore(Path.Combine(root, HookRecorder.DirName));
        var results = store.AllSessionFiles().Select(ChainVerifier.VerifyFile).ToList();
        foreach (var r in results)
        {
            var name = r.Session ?? Path.GetFileNameWithoutExtension(r.File);
            Console.WriteLine(r.Ok ? $"OK    {name}  ({r.Records} records)" : $"FAIL  Session {name}: {r.Error}");
        }
        var broken = results.Count(r => !r.Ok);
        Console.WriteLine(broken == 0
            ? $"{results.Count} session(s) verified. Chains intact (tamper-evident, not tamper-proof)."
            : $"{broken} of {results.Count} session(s) failed verification.");
        return broken == 0 ? 0 : 1;
    }
}
