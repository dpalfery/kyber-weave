using KyberWeave.Contracts.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Mcp;

/// <summary>
/// Runs the schema and drift gates the docs tools report. It stays in the host while the host configuration
/// (<see cref="KyberWeaveConfig"/>) and the schema gate (<see cref="DocsSchemaGate"/>) live in the legacy engine.
/// </summary>
internal sealed class DocsGateRunner : IDocsGateRunner
{
    /// <inheritdoc />
    public void ValidateSchema(
        string repositoryRoot,
        Func<DocumentSet> loadDocuments,
        bool mergeReady,
        string? baseRef,
        DiagnosticReport report)
    {
        if (!HostConfiguration.TryLoad(repositoryRoot, report, out KyberWeaveConfig config))
            return;

        try
        {
            DocumentSet set = loadDocuments();
            report.AddRange(DocsSchemaGate.Validate(
                repositoryRoot,
                config,
                set,
                () => new DocumentLoader(repositoryRoot, config.Ontology),
                mergeReady,
                baseRef).Items);
        }
        catch (Exception exception) when (DocsToolSupport.IsExpectedReadFailure(exception))
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                exception.Message,
                "docs validate",
                Hint: "Fix the repository configuration or corpus access, then call docs_validate_governed_document_schema again."));
        }
    }

    /// <inheritdoc />
    public void CheckDrift(
        string repositoryRoot,
        Func<DocumentSet> loadDocuments,
        Func<ICodeGraphResolver> codeGraph,
        DiagnosticReport report)
    {
        if (!HostConfiguration.TryLoad(repositoryRoot, report, out KyberWeaveConfig _))
            return;

        try
        {
            DocumentSet set = loadDocuments();
            report.AddRange(
                new DocDriftLinter(codeGraph()).Validate(set).Items);
        }
        catch (Exception exception) when (DocsToolSupport.IsExpectedReadFailure(exception))
        {
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                exception.Message,
                "docs drift",
                Hint: "Fix the repository configuration or corpus access, then call docs_detect_drift_between_documents_and_code again."));
        }
    }
}
