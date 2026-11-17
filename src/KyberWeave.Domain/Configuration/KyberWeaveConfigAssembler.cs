using KyberWeave.Core.Arbiter;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// Builds the combined Kyber-Weave configuration from YAML text: the parse, the merge of each section over its defaults,
/// and the checks the combined file adds. Finding and reading the file is the data layer's.
/// </summary>
public static class KyberWeaveConfigAssembler
{
    /// <summary>The repository setting a run reads to decide whether a broken tool stops it.</summary>
    private const string StopOnToolFailureKey = "stop-on-tool-failure";

    /// <summary>The repository setting the git post-checkout hook reads to decide whether a new worktree is indexed.</summary>
    private const string WorktreeIndexHookKey = "worktree-index-hook";

    /// <summary>The setting that chooses the worker roster a run dispatches from.</summary>
    private const string WorkerRosterKey = "worker-roster";

    /// <summary>
    /// Loads configuration from YAML text. There is no repository on disk, so a gate that sets
    /// <c>working-directory</c> is refused.
    /// </summary>
    /// <param name="yaml">The config file's text.</param>
    public static KyberWeaveConfig FromYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        KyberWeaveYamlDocument document = KyberWeaveYamlParser.Parse(yaml);
        return FromDocument(document, yaml, null, null);
    }

    /// <summary>Loads a repository's configuration from YAML text read from a file under <paramref name="repositoryRoot"/>.</summary>
    /// <param name="yaml">The config file's text.</param>
    /// <param name="repositoryRoot">The repository root a gate's <c>working-directory</c> is resolved against.</param>
    /// <param name="facts">What the file system says about a gate's <c>working-directory</c>.</param>
    public static KyberWeaveConfig FromRepositoryYaml(string yaml, string repositoryRoot, ReviewWorkingDirectoryFacts facts)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(facts);
        return FromDocument(KyberWeaveYamlParser.Parse(yaml), yaml, repositoryRoot, facts);
    }

    /// <summary>
    /// Loads the per-user host config text, where <c>arbiter.global.targets</c> is valid and must not be rejected as a
    /// project-scope error.
    /// </summary>
    /// <param name="yaml">The user config file's text.</param>
    public static KyberWeaveConfig FromUserYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        KyberWeaveYamlDocument document = KyberWeaveYamlParser.Parse(yaml);
        OntologyConfig ontology = OntologyConfigLoader.Merge(OntologyConfig.ProductDefaults, document.Ontology);
        return new KyberWeaveConfig
        {
            Ontology = ontology,
            Harness = HarnessProfileConfigLoader.Merge(HarnessProfileConfig.ProductDefaults, document.Harness),
            DocsAnalysis = DocsAnalysisConfigLoader.Merge(
                DocsAnalysisConfig.ProductDefaults,
                document.DocsAnalysis,
                ontology),
            Squad = SquadConfigLoader.Merge(SquadConfig.ProductDefaults, document.Squad),
            ConfigReg = ConfigRegConfigLoader.Merge(
                ConfigRegConfig.ProductDefaults,
                document.ConfigReg,
                document.Autonomy,
                document.Run,
                document.StopOnToolFailure,
                document.Onboarding,
                document.WorkerRoster is null ? null : ParseWorkerRoster(document.WorkerRoster)),
            Review = ReviewConfigParser.Merge(
                ReviewConfig.ProductDefaults,
                document.Review,
                null,
                ReviewConfigParser.UnknownKeyWarnings(yaml)),
            Arbiter = ArbiterConfigLoader.LoadUserConfig(yaml),
            Mcp = McpConfigLoader.Merge(McpConfig.ProductDefaults, document.Mcp),
            StopsOnToolFailure = ParseStopOnToolFailure(document.StopOnToolFailure),
            WorkerRoster = ParseWorkerRoster(document.WorkerRoster)
        };
    }

    private static KyberWeaveConfig FromDocument(KyberWeaveYamlDocument document, string yaml, string? repositoryRoot, ReviewWorkingDirectoryFacts? facts)
    {
        OntologyConfig ontology = OntologyConfigLoader.Merge(OntologyConfig.ProductDefaults, document.Ontology);
        return new KyberWeaveConfig
        {
            Ontology = ontology,
            Harness = HarnessProfileConfigLoader.Merge(HarnessProfileConfig.ProductDefaults, document.Harness),
            DocsAnalysis = DocsAnalysisConfigLoader.Merge(
                DocsAnalysisConfig.ProductDefaults,
                document.DocsAnalysis,
                ontology),
            Squad = SquadConfigLoader.Merge(SquadConfig.ProductDefaults, document.Squad),
            ConfigReg = ConfigRegConfigLoader.Merge(
                ConfigRegConfig.ProductDefaults,
                document.ConfigReg,
                document.Autonomy,
                document.Run,
                document.StopOnToolFailure,
                document.Onboarding,
                document.WorkerRoster is null ? null : ParseWorkerRoster(document.WorkerRoster)),
            Review = ReviewConfigParser.Merge(
                ReviewConfig.ProductDefaults,
                document.Review,
                repositoryRoot,
                ReviewConfigParser.UnknownKeyWarnings(yaml),
                facts),
            Arbiter = ArbiterConfigLoader.Merge(ArbiterConfig.ProductDefaults, document.Arbiter),
            Mcp = McpConfigLoader.Merge(McpConfig.ProductDefaults, document.Mcp),
            StopsOnToolFailure = ParseStopOnToolFailure(document.StopOnToolFailure),
            WorktreeIndexHook = ParseWorktreeIndexHook(document.WorktreeIndexHook),
            WorkerRoster = ParseWorkerRoster(document.WorkerRoster)
        };
    }

    /// <summary>
    /// Reads the top-level <c>worktree-index-hook</c> setting. Absent means on, so a new worktree is indexed by default.
    /// </summary>
    /// <remarks>
    /// Modelled on <see cref="ParseStopOnToolFailure"/>: the raw value is parsed here so a value outside the two booleans
    /// is refused with the key and both accepted values named. The hook is shared by every worktree of the clone, so a
    /// silent guess at an unclear value would change behaviour for all of them without the owner noticing.
    /// </remarks>
    /// <exception cref="YamlException">The value is present and is neither <c>true</c> nor <c>false</c>.</exception>
    private static bool ParseWorktreeIndexHook(string? value)
    {
        if (value is null)
            return true;

        if (bool.TryParse(value, out bool enabled))
            return enabled;

        throw new YamlException(
            $"{WorktreeIndexHookKey} '{value}' is not a boolean. Use 'true' to index a new worktree on its first " +
            "checkout, or 'false' to leave the git hook alone, or remove the key; an absent setting is 'true'.");
    }

    /// <summary>Reads the top-level <c>worker-roster</c> setting. Absent means <c>technology</c>.</summary>
    /// <exception cref="YamlException">The value is neither <c>technology</c> nor <c>tiered</c>.</exception>
    private static string ParseWorkerRoster(string? value)
    {
        if (value is null)
            return WorkerRosters.Technology;

        string roster = value.Trim();
        if (WorkerRosters.IsKnown(roster))
            return roster;

        throw new YamlException(
            $"{WorkerRosterKey} '{value}' is not a roster. Use 'technology' for the per-technology workers, " +
            "or 'tiered' for the three developer agents; remove the key for 'technology'.");
    }

    /// <summary>
    /// Reads the top-level <c>stop-on-tool-failure</c> setting. Absent means stop.
    /// </summary>
    /// <remarks>
    /// The value is parsed here rather than bound as a <see cref="bool"/> on the YAML document, because a
    /// binding failure reports the type it wanted — an operator who wrote <c>stop-on-tool-failure: strict</c>
    /// is told to fix a type they cannot see. This is the same treatment <c>autonomy:</c> gets: the setting
    /// is named, the two values it accepts are named, and the absent case is spelled out.
    /// </remarks>
    /// <exception cref="YamlException">The value is present and is neither <c>true</c> nor <c>false</c>.</exception>
    private static bool ParseStopOnToolFailure(string? value)
    {
        if (value is null)
            return true;

        if (bool.TryParse(value, out bool stops))
            return stops;

        throw new YamlException(
            $"{StopOnToolFailureKey} '{value}' is not a boolean. Use 'true' to stop a run when a " +
            "required tool cannot be repaired, or 'false' to warn and carry on, or remove the key; " +
            "a run treats an absent setting as 'true'.");
    }
}
