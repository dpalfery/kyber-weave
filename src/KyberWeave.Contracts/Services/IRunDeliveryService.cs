namespace KyberWeave.Contracts.Services;

/// <summary>
/// Delivers a finished run: pushes the run branch (never forced) and opens or updates its draft pull request, with the
/// description assembled from what the run recorded.
/// </summary>
public interface IRunDeliveryService
{
    /// <summary>Delivers the run.</summary>
    /// <param name="summary">One to three plain sentences for a human reviewer.</param>
    /// <param name="notes">Extra notes for reviewers, or null.</param>
    /// <param name="title">A title to use instead of the composed one, or null.</param>
    /// <param name="draft">Whether a new pull request opens as a draft.</param>
    /// <param name="documentationImpact">The documentation-impact outcome as docs-dev returned it, or null.</param>
    /// <param name="planFile">The archived plan's file name, only when the branch archived more than one, or null.</param>
    /// <param name="cancellationToken">Cancels the provenance read.</param>
    /// <returns>The tool's complete response text.</returns>
    Task<string> DeliverRunAsync(
        string summary,
        string? notes,
        string? title,
        bool draft,
        string? documentationImpact,
        string? planFile,
        CancellationToken cancellationToken = default);
}
