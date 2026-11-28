using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Contracts.Docs;

/// <summary>The schema and drift gates the docs MCP tools run, with the host configuration they need.</summary>
public interface IDocsGateRunner
{
    /// <summary>Runs the schema conformance gate, adding its findings, and any configuration failure, to the report.</summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="loadDocuments">Loads the corpus the gate validates.</param>
    /// <param name="mergeReady">True to include the merge-ready checks.</param>
    /// <param name="baseRef">The git ref for archive-closeout scope, or null.</param>
    /// <param name="report">The report the findings are added to.</param>
    void ValidateSchema(
        string repositoryRoot,
        Func<DocumentSet> loadDocuments,
        bool mergeReady,
        string? baseRef,
        DiagnosticReport report);

    /// <summary>Runs the entity-drift gate, adding its findings, and any configuration failure, to the report.</summary>
    /// <param name="repositoryRoot">The bound repository root.</param>
    /// <param name="loadDocuments">Loads the corpus the gate checks.</param>
    /// <param name="codeGraph">Builds the CodeGraph port the linter resolves symbols through.</param>
    /// <param name="report">The report the findings are added to.</param>
    /// <param name="cancellationToken">Cancels the index freshness read.</param>
    Task CheckDriftAsync(
        string repositoryRoot,
        Func<DocumentSet> loadDocuments,
        Func<ICodeGraphResolver> codeGraph,
        DiagnosticReport report,
        CancellationToken cancellationToken = default);
}
