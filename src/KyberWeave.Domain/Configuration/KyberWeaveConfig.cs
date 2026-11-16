using KyberWeave.Core.Arbiter;

namespace KyberWeave.Core.Configuration;

/// <summary>Combined Kyber-Weave host configuration.</summary>
public sealed class KyberWeaveConfig
{
    public OntologyConfig Ontology { get; init; } = OntologyConfig.ProductDefaults;

    public HarnessProfileConfig Harness { get; init; } = HarnessProfileConfig.ProductDefaults;

    public DocsAnalysisConfig DocsAnalysis { get; init; } = DocsAnalysisConfig.ProductDefaults;

    public SquadConfig Squad { get; init; } = SquadConfig.ProductDefaults;

    public ConfigRegConfig ConfigReg { get; init; } = ConfigRegConfig.ProductDefaults;

    public ReviewConfig Review { get; init; } = ReviewConfig.ProductDefaults;

    public ArbiterConfig Arbiter { get; init; } = ArbiterConfig.ProductDefaults;

    public McpConfig Mcp { get; init; } = McpConfig.ProductDefaults;

    /// <summary>
    /// Whether a run stops when a required tool — CodeGraph, or the docs server's CodeGraph read —
    /// cannot be repaired.
    /// </summary>
    /// <remarks>
    /// The repository setting is <c>stop-on-tool-failure</c>. Absent means stop, so the default is
    /// <c>true</c>: a repository that says nothing about the setting is the strict case, and
    /// continuing past a broken code index has to be asked for explicitly. The default lives here
    /// rather than on the YAML document so every path that builds a config — a repository file, a
    /// user file, the product defaults, a test fixture — reads the same answer.
    /// </remarks>
    public bool StopsOnToolFailure { get; init; } = true;

    /// <summary>
    /// Whether the git post-checkout hook indexes a new worktree on its first checkout.
    /// </summary>
    /// <remarks>
    /// The repository setting is <c>worktree-index-hook</c>. Absent means on, so a new worktree gets its index
    /// without a setting. Set to <c>false</c> to keep the hook from starting an index run on checkout.
    /// </remarks>
    public bool WorktreeIndexHook { get; init; } = true;

    public static KyberWeaveConfig ProductDefaults { get; } = new();

    public KyberWeaveConfig WithOntology(OntologyConfig ontology)
    {
        ArgumentNullException.ThrowIfNull(ontology);
        return Clone(ontology: ontology);
    }

    public KyberWeaveConfig WithHarness(HarnessProfileConfig harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return Clone(harness: harness);
    }

    public KyberWeaveConfig WithProfiles(HarnessProfileConfig harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return Clone(harness: harness);
    }

    public KyberWeaveConfig WithDocsAnalysis(DocsAnalysisConfig docsAnalysis)
    {
        ArgumentNullException.ThrowIfNull(docsAnalysis);
        return Clone(docsAnalysis: docsAnalysis);
    }

    public KyberWeaveConfig WithSquad(SquadConfig squad)
    {
        ArgumentNullException.ThrowIfNull(squad);
        return Clone(squad: squad);
    }

    public KyberWeaveConfig WithConfigReg(ConfigRegConfig configReg)
    {
        ArgumentNullException.ThrowIfNull(configReg);
        return Clone(configReg: configReg);
    }

    internal KyberWeaveConfig Clone(
        OntologyConfig? ontology = null,
        HarnessProfileConfig? harness = null,
        DocsAnalysisConfig? docsAnalysis = null,
        SquadConfig? squad = null,
        ConfigRegConfig? configReg = null,
        ReviewConfig? review = null,
        ArbiterConfig? arbiter = null,
        McpConfig? mcp = null,
        bool? stopsOnToolFailure = null) =>
        new()
        {
            Ontology = ontology ?? Ontology,
            Harness = harness ?? Harness,
            DocsAnalysis = docsAnalysis ?? DocsAnalysis,
            Squad = squad ?? Squad,
            ConfigReg = configReg ?? ConfigReg,
            Review = review ?? Review,
            Arbiter = arbiter ?? Arbiter,
            Mcp = mcp ?? Mcp,
            StopsOnToolFailure = stopsOnToolFailure ?? StopsOnToolFailure,
            WorktreeIndexHook = WorktreeIndexHook
        };
}
