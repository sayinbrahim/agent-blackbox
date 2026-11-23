using System.Text.RegularExpressions;

namespace Blackbox.Core;

public enum CommandKind { Other, Test, Build, Delete }

public sealed partial class CommandClassifier(BlackboxConfig config)
{
    private readonly Regex[] _extraTest = [.. config.TestCommands.Select(Custom)];
    private readonly Regex[] _extraBuild = [.. config.BuildCommands.Select(Custom)];

    public CommandKind Classify(string command)
    {
        if (Test().IsMatch(command) || _extraTest.Any(r => r.IsMatch(command)))
        {
            return CommandKind.Test;
        }
        if (Build().IsMatch(command) || _extraBuild.Any(r => r.IsMatch(command)))
        {
            return CommandKind.Build;
        }
        return Delete().IsMatch(command) ? CommandKind.Delete : CommandKind.Other;
    }

    private static Regex Custom(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    // Matches at the start of the command or after a separator, so "cd x && dotnet test" counts.
    [GeneratedRegex(@"(?:^|[\s;&|(])(?:dotnet\s+test|npm\s+(?:run\s+)?test|pnpm\s+(?:run\s+)?test|yarn\s+test|pytest|go\s+test|cargo\s+test|mvn\s+test|(?:\./)?gradlew?\s+test)\b")]
    private static partial Regex Test();

    [GeneratedRegex(@"(?:^|[\s;&|(])(?:dotnet\s+build|npm\s+run\s+build|go\s+build|cargo\s+build|mvn\s+package|tsc)\b")]
    private static partial Regex Build();

    [GeneratedRegex(@"(?:^|[\s;&|(])(?:rm|del|rmdir|Remove-Item|git\s+rm)\s", RegexOptions.IgnoreCase)]
    private static partial Regex Delete();
}
