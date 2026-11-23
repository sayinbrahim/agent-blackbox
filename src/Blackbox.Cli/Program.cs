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
        return BuildCommand().Parse(args).Invoke();
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

        var hook = new Command("hook", "Record one Claude Code hook event from stdin (called by Claude Code).") { new Argument<string>("event") };
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
