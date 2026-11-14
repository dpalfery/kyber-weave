using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>
/// Names the directories an agent's installed reference files can be in. A harness projects an agent's
/// resources beside its agent file, or beside its entry-point skill when the agent is lowered onto one, so the
/// directory is <c>&lt;root&gt;/&lt;folder&gt;/&lt;agent&gt;/references</c> or, for the lowered entry points of some
/// harnesses, <c>&lt;root&gt;/&lt;folder&gt;/&lt;agent&gt;/&lt;agent&gt;/references</c>. The root is the harness's project directory at
/// project scope and its global root at global scope.
/// </summary>
public static class SquadReferenceLocator
{
    private const string References = "references";

    /// <summary>The folders under a harness root that hold agents or the skills an agent is lowered onto.</summary>
    private static readonly string[] Folders = ["agents", "droids", "skills"];

    /// <summary>
    /// Each harness's project-scope root directory. The distribution test renders the squad for every harness
    /// and fails when a deployed reference file is not in a directory <see cref="Candidates"/> names.
    /// </summary>
    internal static IReadOnlyDictionary<SquadTarget, string> ProjectRoots { get; } =
        new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Claude] = ".claude",
            [SquadTarget.Codex] = ".codex",
            [SquadTarget.Cursor] = ".cursor",
            [SquadTarget.Copilot] = ".github",
            [SquadTarget.Antigravity] = ".agents",
            [SquadTarget.Pi] = ".pi",
            [SquadTarget.OpenCode] = ".opencode",
            [SquadTarget.Kilo] = ".kilo",
            [SquadTarget.Warp] = ".warp",
            [SquadTarget.Factory] = ".factory",
            [SquadTarget.ZCode] = ".zcode",
            [SquadTarget.Devin] = ".devin",
            [SquadTarget.CommandCode] = ".commandcode",
        };

    /// <summary>
    /// The candidate directories in the order they are tried: project scope under each of
    /// <paramref name="projectRoots"/>, then global scope. Without a <paramref name="target"/> every
    /// harness is a candidate, in catalogue order.
    /// </summary>
    /// <param name="agent">The agent whose references are wanted.</param>
    /// <param name="target">The harness, or null when it is not known.</param>
    /// <param name="projectRoots">The checkouts to look under at project scope, most specific first.</param>
    /// <param name="globalRoots">Resolves each harness's global root, or null to skip global scope.</param>
    public static IReadOnlyList<string> Candidates(
        string agent,
        SquadTarget? target,
        IEnumerable<string> projectRoots,
        ISquadGlobalRootResolver? globalRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentNullException.ThrowIfNull(projectRoots);

        SquadTarget[] targets = target is { } one ? [one] : [.. ProjectRoots.Keys];
        string[] roots = [.. projectRoots];
        List<string> candidates = [];
        foreach (SquadTarget each in targets)
        {
            candidates.AddRange(roots.SelectMany(root => Under(Path.Combine(root, ProjectRoots[each]), agent)));
        }

        if (globalRoots is not null)
        {
            foreach (SquadTarget each in targets)
            {
                candidates.AddRange(Under(globalRoots.ResolveGlobalRoot(each), agent));
            }
        }

        return candidates;
    }

    private static IEnumerable<string> Under(string root, string agent) =>
        Folders.SelectMany(folder => new[]
        {
            Path.Combine(root, folder, agent, References),
            Path.Combine(root, folder, agent, agent, References),
        });

    /// <summary>The reference directory of <paramref name="agent"/> in the product source tree.</summary>
    public static string InSource(string productSourceDirectory, string agent) =>
        Path.Combine(productSourceDirectory, "agents", agent, References);
}
