using KyberWeave.Core.Utilities.StatusLine;

namespace KyberWeave.Cli.Commands.Utilities;

/// <summary>Loads the Kyber-owned status-line artifacts a deploy will stage for one harness.</summary>
/// <remarks>
///     A seam, not a policy: it decides where the canonical artifacts are read from, and the deploy
///     command decides what to do with them. Production reads the product tree; a test substitutes a
///     temporary directory so the whole deploy surface can be driven without the real artifacts, which
///     T5 imports separately.
/// </remarks>
internal interface IUtilitiesStatusLineArtifactSource
{
    /// <summary>
    ///     Every file to own for <paramref name="target" />, as staging-root-relative paths and bytes.
    /// </summary>
    /// <exception cref="IOException">The artifact set for this harness cannot be read.</exception>
    IReadOnlyList<StatusLineDeploymentFile> Load(StatusLineTarget target);
}

/// <summary>The per-target facts the deploy and doctor commands need about an artifact set.</summary>
internal static class UtilitiesStatusLineArtifacts
{
    /// <summary>
    ///     The file a harness's activation snippet names, relative to the target's artifact directory.
    /// </summary>
    /// <remarks>
    ///     Claude and <c>agy</c> run a command, so the snippet names the script; Pi loads an extension,
    ///     so it names the module. The plan and the artifact contract suite pin these names, so a change
    ///     here moves together with <c>products/kyber-utilities/statusline/</c>.
    /// </remarks>
    public static string PrimaryFileName(StatusLineTarget target)
    {
        return target switch
        {
            StatusLineTarget.Claude => "statusline.sh",
            StatusLineTarget.Agy => "statusline.py",
            StatusLineTarget.Pi => "statusbar.ts",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No primary status-line artifact name exists for this target.")
        };
    }

    /// <summary>
    ///     Whether a deployed file must carry the executable bit.
    /// </summary>
    /// <remarks>
    ///     The two command variants are run directly by their harness and must be executable; Pi's
    ///     extension is loaded by Node and must not be. Doctor checks the same predicate a deploy
    ///     applies, so the two cannot disagree about which files the bit is owed to.
    /// </remarks>
    public static bool RequiresExecutableBit(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        return extension is ".sh" or ".py";
    }
}
