using Spectre.Console;

namespace KyberWeave.Cli.Rendering;

/// <summary>Keeps a console's render width usable when the host cannot report one.</summary>
/// <remarks>
/// .NET reports <c>Console.BufferWidth</c> as -1 when the terminal size is undetectable — a
/// container with <c>TERM</c> set but no matching terminfo entry, for instance. Spectre only
/// substitutes its 80-column default for 0, so -1 reaches the profile as the render width:
/// every markup line then renders empty and table cells collapse to an ellipsis, which makes
/// the CLI print nothing on exactly the hosts where its output is most often captured.
/// </remarks>
public static class ConsoleWidth
{
    /// <summary>Spectre's own default width for a console that cannot measure itself.</summary>
    public const int Default = 80;

    /// <summary>Pins <paramref name="console"/> to <see cref="Default"/> when its width is not positive.</summary>
    public static void EnsureUsable(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (console.Profile.Width <= 0)
        {
            console.Profile.Width = Default;
        }
    }
}
