using JetBrains.Annotations;
using KyberWeave.Cli.Rendering;
using Spectre.Console;

namespace KyberWeave.Tests;

/// <summary>
/// Serializes tests that temporarily replace process-global console state without
/// disabling parallel execution for unrelated test work.
/// </summary>
internal static class ProcessConsoleCapture
{
    private static readonly Lock Gate = new();

    /// <summary>
    /// Runs <paramref name="execute"/> once, before returning, with console output captured,
    /// holding the process-wide console lock for the duration.
    /// </summary>
    public static CapturedConsoleExecution<T> Run<T>([InstantHandle] Func<T> execute)
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
                IAnsiConsole capture = AnsiConsole.Create(new AnsiConsoleSettings
                {
                    Ansi = AnsiSupport.No,
                    ColorSystem = ColorSystemSupport.NoColors,
                    Interactive = InteractionSupport.No,
                    Out = new AnsiConsoleOutput(writer)
                });
                // The capture's width is otherwise the host terminal's, which is -1 on a
                // host that cannot report one; pin it the way the CLI entry point does.
                ConsoleWidth.EnsureUsable(capture);
                AnsiConsole.Console = capture;
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
