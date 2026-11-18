namespace KyberWeave.Contracts.Repositories;

/// <summary>Lists the companion skills shipped beside a Squad source tree.</summary>
/// <remarks>
/// The skills live in <c>.apm/skills</c> of the repository that holds <c>products/kyber-squad</c>. Listing them is a
/// read of an outside resource, so it is a port: the archive composing above the seams asks this for the files and
/// reads them through the file seam.
/// </remarks>
public interface ISquadCompanionSkills
{
    /// <summary>Lists every file of every companion skill.</summary>
    /// <param name="squadSourceRoot">The <c>products/kyber-squad</c> directory.</param>
    /// <param name="companionSkillsRoot">The companion skills root, or null.</param>
    /// <returns>Each file's Squad-tree path and its source path; empty when there is no companion root.</returns>
    /// <exception cref="Core.Squad.Validation.SquadSourceValidationException">
    /// A companion name clashes with a Squad skill or agent, or a symbolic link would be read through.
    /// </exception>
    IReadOnlyList<(string RelativePath, string SourcePath)> Collect(
        string squadSourceRoot,
        string? companionSkillsRoot);
}
