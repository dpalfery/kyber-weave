using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Utilities.StatusLine;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Shared console rendering for the status-line commands.</summary>
/// <remarks>
///     The activation snippet is printed line by line through <see cref="AnsiConsole.WriteLine(string)" />
///     rather than as markup: the snippet is JSON the operator copies verbatim, so it must not be
///     re-parsed for markup, and a path containing brackets must survive intact.
/// </remarks>
internal static class UtilitiesStatusLineOutput
{
    /// <summary>Prints the per-harness activation snippet for a deployed artifact.</summary>
    public static void PrintActivationGuidance(StatusLineTarget target, string commandAbsolutePath)
    {
        string token = UtilitiesStatusLineTargetCatalog.GetToken(target);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Activation — {Markup.Escape(token)}[/]");
        foreach (string line in UtilitiesStatusLineActivationGuidance.Build(target, commandAbsolutePath))
            AnsiConsole.WriteLine(line);

        AnsiConsole.WriteLine(UtilitiesStatusLineActivationGuidance.ApplyYourselfLine);
    }

    /// <summary>
    ///     The absolute path of the artifact a harness's snippet names, or <see langword="null" /> when the
    ///     artifact set does not carry it.
    /// </summary>
    public static string? ResolvePrimaryCommandPath(
        StatusLineTarget target,
        string stagingRoot,
        IReadOnlyList<StatusLineDeploymentFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        string primaryFileName = UtilitiesStatusLineArtifacts.PrimaryFileName(target);
        foreach (StatusLineDeploymentFile file in files)
            if (string.Equals(Path.GetFileName(file.RelativePath), primaryFileName, StringComparison.Ordinal))
                return SquadPathPolicy.ResolveFile(stagingRoot, file.RelativePath);

        return null;
    }
}
