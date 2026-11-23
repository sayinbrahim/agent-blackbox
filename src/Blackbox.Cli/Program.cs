using System.Reflection;

namespace Blackbox.Cli;

public static class Program
{
    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "hook")
        {
            // Recorder must never break the agent: always exit 0, never write to stdout. (Recording lands in Milestone 1.)
            return 0;
        }

        if (args.Length > 0 && args[0] is "--version" or "-v")
        {
            Console.WriteLine($"bbx {Version}");
            return 0;
        }

        Console.WriteLine($"bbx {Version} - a flight recorder for AI coding agents");
        Console.WriteLine("Commands arrive in Milestone 1+. See README.md.");
        return 0;
    }
}
