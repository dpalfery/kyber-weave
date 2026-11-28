using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Mcp;

/// <summary>
/// Runs the schema and drift gates the docs tools report.
/// </summary>
/// <param name="hostConfiguration">Loads the repository's host configuration.</param>
/// <param name="schemaGate">The schema-validation sequence the CLI shares.</param>
/// <param name="documents">Opens the corpus again for the archive-closeout gate.</param>
public sealed class DocsGateRunner(
    IDocsHostConfiguration hostConfiguration,
    IDocsSchemaGate schemaGate,
    IDocumentSourceFactory documents) : IDocsGateRunner
{
    /// <inheritdoc />
    public void ValidateSchema(
        string repositoryRoot,
        Func<DocumentSet> loadDocuments,
        bool mergeReady,
        string? baseRef,
        DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(loadDocuments);
        ArgumentNullException.ThrowIfNull(report);

        if (!hostConfiguration.TryLoad(repositoryRoot, report, out KyberWeaveConfig config))
            return;

        try
        {
            DocumentSet set = loadDocuments();
            report.AddRange(schemaGate.Validate(
                repositoryRoot,
                config,
                set,
                () => documents.Create(repositoryRoot, config.Ontology),
                mergeReady,
                baseRef).Items);
        }
        catch (Exception exception) when (DocsToolSupport.IsExpectedReadFailure(exception))
        {
            report.Add(new Diagnostic(
                IDocsHostConfiguration.ConfigLoadErrorCode,
                Severity.Error,
                exception.Message,
                "docs validate",
                Hint: "Fix the repository configuration or corpus access, then call docs_validate_governed_document_schema again."));
        }
    }

    /// <inheritdoc />
    public async Task CheckDriftAsync(
        string repositoryRoot,
        Func<DocumentSet> loadDocuments,
        Func<ICodeGraphResolver> codeGraph,
        DiagnosticReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadDocuments);
        ArgumentNullException.ThrowIfNull(codeGraph);
        ArgumentNullException.ThrowIfNull(report);

        if (!hostConfiguration.TryLoad(repositoryRoot, report, out KyberWeaveConfig _))
            return;

        try
        {
            DocumentSet set = loadDocuments();
            DiagnosticReport drift = await new DocDriftLinter(codeGraph())
                .ValidateAsync(set, cancellationToken)
                .ConfigureAwait(false);
            report.AddRange(drift.Items);
        }
        catch (Exception exception) when (DocsToolSupport.IsExpectedReadFailure(exception))
        {
            report.Add(new Diagnostic(
                IDocsHostConfiguration.ConfigLoadErrorCode,
                Severity.Error,
                exception.Message,
                "docs drift",
                Hint: "Fix the repository configuration or corpus access, then call docs_detect_drift_between_documents_and_code again."));
        }
    }
}
