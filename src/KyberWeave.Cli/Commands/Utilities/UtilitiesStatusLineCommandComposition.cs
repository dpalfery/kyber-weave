using KyberWeave.Core.Processes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Utilities.StatusLine;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>
///     Composition root for the <c>utilities statusline</c> branch: it decides which roots, artifact
///     source, and process runner the commands use, so Core and the commands stay free of process state.
/// </summary>
internal static class UtilitiesStatusLineCommandComposition
{
    /// <summary>How long a harness version probe may run before doctor gives up on it.</summary>
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Writes the exit-2 client-input error line, following the Squad branch's wording.</summary>
    public static void WriteClientInputError(string message)
    {
        AnsiConsole.MarkupLine($"[red]kyber-weave utilities statusline: error: {Markup.Escape(message)}[/]");
    }

    /// <summary>
    ///     Resolves the per-user roots from the process environment and the operator's home directory.
    /// </summary>
    public static StatusLineTargetRoots ResolveRoots()
    {
        return new StatusLineTargetRoots(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>Resolves the tool-presence probe over the process environment.</summary>
    public static UtilitiesStatusLineToolProbe ResolveToolProbe()
    {
        return new UtilitiesStatusLineToolProbe(Environment.GetEnvironmentVariable);
    }

    /// <summary>The production process runner, with the harness version probe's bounded wait.</summary>
    public static UtilitiesStatusLineProcessRunner ResolveProcessRunner()
    {
        return static (startInfo, standardInput) => ProcessRunner.Run(startInfo, standardInput, VersionProbeTimeout);
    }

    /// <summary>
    ///     Resolves the artifact source from the current repository checkout.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The checkout does not hold the artifact tree.</exception>
    public static IUtilitiesStatusLineArtifactSource ResolveArtifactSource()
    {
        string? productRoot = UtilitiesStatusLineProductLocator.Resolve(Directory.GetCurrentDirectory());
        if (productRoot is null)
            throw new DirectoryNotFoundException(
                "The Kyber Utilities status-line artifacts were not found. Run this command from the " +
                "repository checkout that holds 'products/kyber-utilities/statusline'.");

        return new UtilitiesStatusLineProductArtifactSource(productRoot);
    }

    /// <summary>
    ///     Marks the deployed command variants executable.
    /// </summary>
    /// <remarks>
    ///     The deployment core writes bytes, not modes, so the executable bit the two command variants
    ///     need is applied here after <see cref="StatusLineDeploymentPlan.Apply" />. Pi's extension is
    ///     loaded by Node and is left as written. The slice is macOS and Linux only (D4), so the mode is
    ///     a no-op on a platform that has no such bit.
    /// </remarks>
    public static void ApplyExecutableBit(
        string stagingRoot,
        IReadOnlyList<StatusLineDeploymentFile> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentNullException.ThrowIfNull(files);

        if (OperatingSystem.IsWindows()) return;

        foreach (StatusLineDeploymentFile file in files)
        {
            if (!UtilitiesStatusLineArtifacts.RequiresExecutableBit(file.RelativePath)) continue;

            string physicalPath = SquadPathPolicy.ResolveFile(stagingRoot, file.RelativePath);
            if (!File.Exists(physicalPath)) continue;

            UnixFileMode mode = File.GetUnixFileMode(physicalPath);
            File.SetUnixFileMode(
                physicalPath,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    /// <summary>The physical path of a receipt-owned file beneath the staging root.</summary>
    public static string ResolveOwnedPhysicalPath(string stagingRoot, string relativePath)
    {
        return SquadPathPolicy.ResolveFile(stagingRoot, relativePath);
    }

    /// <summary>
    ///     Whether a deployed file carries the user-execute bit. Always true on a platform with no such
    ///     bit, so doctor does not flag a file it cannot check (the slice is macOS and Linux only, D4).
    /// </summary>
    public static bool HasExecutableBit(string physicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);

        return OperatingSystem.IsWindows() ||
               (File.GetUnixFileMode(physicalPath) & UnixFileMode.UserExecute) != 0;
    }
}
