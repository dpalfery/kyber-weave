using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Contracts.Services;

/// <summary>
/// The schema-validation sequence the CLI and the docs MCP server share, so the two surfaces cannot diverge on which
/// validators run or in what order.
/// </summary>
public interface IDocsSchemaGate
{
    /// <summary>
    /// Runs every frontmatter and inventory validator, the review guidance's severity overrides, the managed glossary
    /// gate, and, only when <paramref name="mergeReady"/> is set, the open-work and archive-closeout gates.
    /// </summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="config">The effective configuration.</param>
    /// <param name="corpus">The loaded corpus.</param>
    /// <param name="closeoutLoaderFactory">Opens the corpus again for the archive-closeout gate.</param>
    /// <param name="mergeReady">Whether to run the merge-readiness gates.</param>
    /// <param name="gitBase">The ref the working tree is compared with, or null for the default.</param>
    DiagnosticReport Validate(
        string repoRoot,
        KyberWeaveConfig config,
        DocumentSet corpus,
        Func<IDocumentSource> closeoutLoaderFactory,
        bool mergeReady,
        string? gitBase = null);
}
