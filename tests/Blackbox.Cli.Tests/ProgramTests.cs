using Blackbox.Cli;

namespace Blackbox.Cli.Tests;

public class ProgramTests
{
    [Fact]
    public void Hook_ExitsZero_AndWritesNothingToStdout()
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, Program.Main(["hook", "PostToolUse"]));
        }
        finally
        {
            Console.SetOut(original);
        }
        Assert.Equal(string.Empty, captured.ToString());
    }
}
