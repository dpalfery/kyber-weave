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
    /// <returns>The tool's complete response text.</returns>
    string FinalizePlan(string planFile, string approvalMode, string approval);

    /// <summary>Archives a plan whose closeout evidence is complete, refusing with closeout's findings otherwise.</summary>
    /// <param name="planFile">The plan file, as a path or its file name under the plans folder.</param>
    /// <returns>The tool's complete response text.</returns>
    string ArchivePlan(string planFile);
}
