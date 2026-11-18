using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>
///     Composition root for the <c>utilities statusline</c> branch: it decides which roots, artifact
///     source, and process runner the commands use, so Core and the commands stay free of process state.
/// </summary>
internal static class UtilitiesStatusLineCommandComposition
{
    /// <summary>Writes the exit-2 client-input error line, following the Squad branch's wording.</summary>
    /// <param name="console">Receives the refusal.</param>
    /// <param name="message">The client-input error to report.</param>
    public static void WriteClientInputError(IAnsiConsole console, string message)
    {
        ArgumentNullException.ThrowIfNull(console);

        console.MarkupLine($"[red]kyber-weave utilities statusline: error: {Markup.Escape(message)}[/]");
    }

    /// <summary>
    ///     Resolves the per-user roots from the process environment and the operator's home directory.
    /// </summary>
    /// <param name="environment">The process state the roots are read from.</param>
    public static StatusLineTargetRoots ResolveRoots(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return new StatusLineTargetRoots(environment.GetVariable, environment.HomeDirectory);
    }

    /// <summary>The physical path of a receipt-owned file beneath the staging root.</summary>
    /// <param name="stagingRoot">The harness staging root the receipt was deployed under.</param>
    /// <param name="relativePath">The portable relative path the receipt records.</param>
    /// <param name="semantics">The filesystem's containment rules.</param>
    /// <returns>The absolute path the file resolves to.</returns>
    public static string ResolveOwnedPhysicalPath(
        string stagingRoot,
        string relativePath,
        IFileSystemPathSemantics semantics)
    {
        return SquadPathPolicy.ResolveFile(stagingRoot, relativePath, semantics);
    }
}
