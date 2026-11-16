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
    public static void WriteClientInputError(string message)
    {
        AnsiConsole.MarkupLine($"[red]kyber-weave utilities statusline: error: {Markup.Escape(message)}[/]");
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
    public static string ResolveOwnedPhysicalPath(string stagingRoot, string relativePath)
    {
        return SquadPathPolicy.ResolveFile(stagingRoot, relativePath);
    }
}
