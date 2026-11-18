using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;

namespace KyberWeave.Core.Docs.Closeout;

/// <summary>Runs <see cref="PlanCloseout"/> over the ports the container holds.</summary>
public sealed class PlanCloseoutService(
    IRepositoryFileStore files,
    IBoundedProcessExecutor process,
    IExclusiveFileWriter exclusive,
    IDocumentSourceFactory documents,
    IPathResolver paths,
    IProcessEnvironment environment) : IPlanCloseoutService
{
    /// <inheritdoc />
    public PlanCloseoutOutcome Run(string repoRoot, KyberWeaveConfig config, string planPath, DateOnly archiveDate, bool archive)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        return new PlanCloseout(repoRoot, config, () => documents.Create(repoRoot, config.Ontology), files, process, exclusive, paths, environment)
            .Run(planPath, archiveDate, archive);
    }
}

/// <summary>Runs <see cref="PlanFinalize"/> over the ports the container holds.</summary>
public sealed class PlanFinalizeService(
    IRepositoryFileStore files,
    IBoundedProcessExecutor process,
    IDocsSchemaGate gate,
    IDocumentSourceFactory documents,
    IPlanTaskAgentPickerFactory pickers,
    IPathResolver paths,
    IProcessEnvironment environment) : IPlanFinalizeService
{
    /// <inheritdoc />
    public PlanFinalizeOutcome Run(
        string repoRoot,
        KyberWeaveConfig config,
        string planPath,
        PlanApprovalMode mode,
        string? approval,
        DateOnly approvalDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        // The decision model, when one is configured, picks each unassigned task's agent as the task file is written.
        using IPlanTaskAgentPicker? picker = pickers.ForConfig(config);
        return new PlanFinalize(repoRoot, config, () => documents.Create(repoRoot, config.Ontology), files, process, gate, paths, environment, picker)
            .Run(planPath, mode, approval, approvalDate);
    }
}
