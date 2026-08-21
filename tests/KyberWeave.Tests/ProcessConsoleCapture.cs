using Spectre.Console;

namespace KyberWeave.Tests;

/// <summary>
/// Serializes tests that temporarily replace process-global console state without
/// disabling parallel execution for unrelated test work.
/// </summary>
internal static class ProcessConsoleCapture
{
    private static readonly Lock Gate = new();

    public static CapturedConsoleExecution<T> Run<T>(Func<T> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        lock (Gate)
        {
            using StringWriter writer = new StringWriter();
            TextWriter originalOut = Console.Out;
            IAnsiConsole originalAnsiConsole = AnsiConsole.Console;
            try
            {
                Console.SetOut(writer);
                AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
                {
                    Ansi = AnsiSupport.No,
                    ColorSystem = ColorSystemSupport.NoColors,
                    Interactive = InteractionSupport.No,
                    Out = new AnsiConsoleOutput(writer)
                });
                return new CapturedConsoleExecution<T>(execute(), writer.ToString());
            }
            finally
            {
                AnsiConsole.Console = originalAnsiConsole;
                Console.SetOut(originalOut);
            }
        }
    }
}

internal sealed record CapturedConsoleExecution<T>(T Result, string Output);
