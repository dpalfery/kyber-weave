using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>What a brief is built from. Facts left null are resolved by the machine where they can be.</summary>
/// <param name="Agent">The agent whose reference files are read.</param>
/// <param name="Path">The route, or null.</param>
/// <param name="Autonomy">The autonomy for this run, or null to read the repository's.</param>
/// <param name="Mode">The development mode, or null to read the plan's.</param>
/// <param name="Plan">A plan file in any spelling the plan resolver accepts, or null.</param>
/// <param name="Harness">The harness the references are installed for, or null to try every harness.</param>
/// <param name="From">A directory holding the reference files, or null to find the installed ones.</param>
/// <param name="RepositoryRoot">The repository root the configuration and plans are read from.</param>
/// <param name="ProjectRoots">The checkouts to look under for project-scope installs, most specific first.</param>
/// <param name="Config">The loaded configuration, or null when it did not load.</param>
public sealed record SquadBriefRequest(
    string Agent,
    string? Path,
    string? Autonomy,
    string? Mode,
    string? Plan,
    string? Harness,
    string? From,
    string RepositoryRoot,
    IReadOnlyList<string> ProjectRoots,
    KyberWeaveConfig? Config);
