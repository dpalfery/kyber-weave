using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Shared console rendering for the status-line commands.</summary>
/// <remarks>
///     The activation snippet is printed line by line through <see cref="IAnsiConsole.WriteLine(string)" />
///     rather than as markup: the snippet is JSON the operator copies verbatim, so it must not be
///     re-parsed for markup, and a path containing brackets must survive intact.
/// </remarks>
internal static class UtilitiesStatusLineOutput
{
    /// <summary>Prints the per-harness activation snippet for a deployed artifact.</summary>
    /// <param name="console">Receives the snippet.</param>
    /// <param name="target">The harness the snippet activates.</param>
    /// <param name="commandAbsolutePath">The absolute path of the deployed artifact.</param>
    public static void PrintActivationGuidance(IAnsiConsole console, StatusLineTarget target, string commandAbsolutePath)
    {
        ArgumentNullException.ThrowIfNull(console);

        string token = UtilitiesStatusLineTargetCatalog.GetToken(target);
        console.WriteLine();
        console.MarkupLine($"[bold]Activation — {Markup.Escape(token)}[/]");
        foreach (string line in UtilitiesStatusLineActivationGuidance.Build(target, commandAbsolutePath))
            console.WriteLine(line);

        console.WriteLine(UtilitiesStatusLineActivationGuidance.ApplyYourselfLine);
    }

    /// <summary>
    ///     The absolute path of the artifact a harness's snippet names, or <see langword="null" /> when the
    ///     artifact set does not carry it.
    /// </summary>
    /// <param name="target">The harness the artifact set belongs to.</param>
    /// <param name="stagingRoot">The harness staging root the deployment writes beneath.</param>
    /// <param name="files">The artifacts the deployment owns.</param>
    /// <param name="semantics">The filesystem's containment rules.</param>
    /// <returns>The absolute path, or null when the set does not carry the primary file.</returns>
    public static string? ResolvePrimaryCommandPath(
        StatusLineTarget target,
        string stagingRoot,
        IReadOnlyList<StatusLineDeploymentFile> files,
        IFileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(files);

        string primaryFileName = UtilitiesStatusLineArtifacts.PrimaryFileName(target);
        foreach (StatusLineDeploymentFile file in files)
            if (string.Equals(Path.GetFileName(file.RelativePath), primaryFileName, StringComparison.Ordinal))
                return SquadPathPolicy.ResolveFile(stagingRoot, file.RelativePath, semantics);

        return null;
    }
}
