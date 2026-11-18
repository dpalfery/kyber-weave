namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the shared worker baseline block from a Squad source tree.</summary>
public interface ISquadWorkerBaselineFile
{
    /// <summary>Reads the injection block from a Squad source tree, or returns null when the tree carries no baseline file.</summary>
    /// <param name="sourceDirectory">The Squad source root the render loaded from.</param>
    /// <returns>The block, tags included, or null when the tree carries no baseline file.</returns>
    string? ReadBlock(string sourceDirectory);
}
