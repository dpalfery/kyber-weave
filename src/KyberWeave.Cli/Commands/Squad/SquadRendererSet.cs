using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>The shipped harness renderers.</summary>
internal static class SquadRendererSet
{
    /// <summary>
    /// Creates one instance of every shipped harness renderer. The cross-harness contract suite runs against this
    /// same list, so a renderer added here inherits that contract without anyone remembering to copy it.
    /// </summary>
    internal static IReadOnlyList<ISquadRenderer> Create() =>
    [
        new CopilotRenderer(),
        new CursorRenderer(),
        new ClaudeRenderer(),
        new AntigravityRenderer(),
        new CodexRenderer(),
        new OpenCodeRenderer(),
        new KiloRenderer(),
        new PiRenderer(),
        new FactoryRenderer(),
        new WarpRenderer(),
        new ZCodeRenderer(),
        new DevinRenderer(),
        new CommandCodeRenderer()
    ];
}

/// <summary>What a Squad command reads from the console: whether it can prompt, and how it reads an answer.</summary>
/// <param name="IsInteractive">Whether the console can prompt; null means ask the console.</param>
/// <param name="ReadAnswer">Reads a yes or no answer to a prompt; null means read the console.</param>
internal sealed record SquadConsoleOptions(bool? IsInteractive = null, Func<string, bool>? ReadAnswer = null);
