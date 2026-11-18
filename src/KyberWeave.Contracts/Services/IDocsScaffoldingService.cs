using KyberWeave.Core.Docs.Scaffolding;

namespace KyberWeave.Contracts.Services;

/// <summary>Bootstraps a host repository into a governable documentation corpus.</summary>
public interface IDocsScaffoldingService
{
    /// <summary>
    /// Writes the host configuration, the component catalogue, the ontology reference, one
    /// index per scaffolded folder, and a draft coding standard per declared technology.
    /// </summary>
    /// <param name="repoRoot">The repository to scaffold into.</param>
    /// <param name="docsRoot">The documentation root; null reads it from configuration, else by convention.</param>
    /// <param name="owner">The owner every generated document names.</param>
    /// <param name="force">Whether existing hand-authored files may be overwritten.</param>
    /// <param name="kyberStandards">Whether every technology Kyber-Weave ships a standard for is declared.</param>
    /// <param name="reviewGates">Whether the catalogue review gates are rendered into the host configuration.</param>
    /// <param name="standards">
    /// The technology, role and platform template folders to install, or null to install only
    /// what the configuration already declares. Each name is declared in the ontology.
    /// </param>
    /// <returns>What was created, updated, skipped and preserved.</returns>
    /// <exception cref="ArgumentException">The docs root escapes the repository, or the owner cannot be emitted.</exception>
    /// <exception cref="InvalidDataException">The host configuration exists and cannot be read.</exception>
    /// <exception cref="IOException">A write fails.</exception>
    ScaffoldResult Scaffold(
        string repoRoot,
        string? docsRoot = null,
        string owner = "unassigned",
        bool force = false,
        bool kyberStandards = false,
        bool reviewGates = true,
        IReadOnlyList<string>? standards = null);
}
