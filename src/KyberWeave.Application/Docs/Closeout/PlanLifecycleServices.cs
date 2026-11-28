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
        return CreateCloseout(repoRoot, config).Run(planPath, archiveDate, archive);
    }

    /// <inheritdoc />
    public Task<PlanCloseoutOutcome> RunAsync(
        string repoRoot,
        KyberWeaveConfig config,
        string planPath,
        DateOnly archiveDate,
        bool archive,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        return CreateCloseout(repoRoot, config).RunAsync(planPath, archiveDate, archive, cancellationToken);
    }

    private PlanCloseout CreateCloseout(string repoRoot, KyberWeaveConfig config) =>
        new(repoRoot, config, () => documents.Create(repoRoot, config.Ontology), files, process, exclusive, paths, environment);
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
        return CreateFinalize(repoRoot, config, picker).Run(planPath, mode, approval, approvalDate);
    }

    /// <inheritdoc />
    public async Task<PlanFinalizeOutcome> RunAsync(
        string repoRoot,
        KyberWeaveConfig config,
        string planPath,
        PlanApprovalMode mode,
        string? approval,
        DateOnly approvalDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        // The decision model, when one is configured, picks each unassigned task's agent as the task file is written.
        using IPlanTaskAgentPicker? picker = await pickers.ForConfigAsync(config, cancellationToken).ConfigureAwait(false);
        return await CreateFinalize(repoRoot, config, picker)
            .RunAsync(planPath, mode, approval, approvalDate, cancellationToken)
            .ConfigureAwait(false);
    }

    private PlanFinalize CreateFinalize(string repoRoot, KyberWeaveConfig config, IPlanTaskAgentPicker? picker) =>
        new(repoRoot, config, () => documents.Create(repoRoot, config.Ontology), files, process, gate, paths, environment, picker);
}
