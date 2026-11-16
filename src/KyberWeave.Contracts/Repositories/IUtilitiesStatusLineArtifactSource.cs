using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Loads the Kyber-owned status-line artifacts a deploy will stage for one harness.</summary>
/// <remarks>
///     A seam, not a policy: it decides where the canonical artifacts are read from, and the deploy
///     command decides what to do with them. Production reads the product tree; a test substitutes a
///     temporary directory so the whole deploy surface can be driven without the real artifacts, which
///     T5 imports separately.
/// </remarks>
public interface IUtilitiesStatusLineArtifactSource
{
    /// <summary>
    ///     Every file to own for <paramref name="target" />, as staging-root-relative paths and bytes.
    /// </summary>
    /// <exception cref="IOException">The artifact set for this harness cannot be read.</exception>
    IReadOnlyList<StatusLineDeploymentFile> Load(StatusLineTarget target);
}
