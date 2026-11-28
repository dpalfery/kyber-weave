namespace KyberWeave.Contracts.Services;

/// <summary>
/// The two plan lifecycle operations the docs MCP server exposes: finalize an approved Draft plan, and archive a plan
/// whose closeout evidence is complete. They are the docs server's only operations that change files, and each changes
/// only a plan, its task file, the plan index and the archive.
/// </summary>
public interface IPlanLifecycleService
{
    /// <summary>Records the approval, sets the plan Ready, writes its task file and commits those files (never pushing).</summary>
    /// <param name="planFile">The plan file, as a path or its file name under the plans folder.</param>
    /// <param name="approvalMode">'autonomous' or 'supervised'.</param>
    /// <param name="approval">One line saying what approved it.</param>
    /// <param name="cancellationToken">Cancels the provenance read.</param>
    /// <returns>The tool's complete response text.</returns>
    Task<string> FinalizePlanAsync(
        string planFile,
        string approvalMode,
        string approval,
        CancellationToken cancellationToken = default);

    /// <summary>Archives a plan whose closeout evidence is complete, refusing with closeout's findings otherwise.</summary>
    /// <param name="planFile">The plan file, as a path or its file name under the plans folder.</param>
    /// <param name="cancellationToken">Cancels the provenance read.</param>
    /// <returns>The tool's complete response text.</returns>
    Task<string> ArchivePlanAsync(string planFile, CancellationToken cancellationToken = default);
}
