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

    public KyberWeaveConfig WithReview(ReviewConfig review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return Clone(review: review);
    }

    internal KyberWeaveConfig Clone(
        OntologyConfig? ontology = null,
        HarnessProfileConfig? harness = null,
        DocsAnalysisConfig? docsAnalysis = null,
        SquadConfig? squad = null,
        ConfigRegConfig? configReg = null,
        ReviewConfig? review = null) =>
        new()
        {
            Ontology = ontology ?? Ontology,
            Harness = harness ?? Harness,
            DocsAnalysis = docsAnalysis ?? DocsAnalysis,
            Squad = squad ?? Squad,
            ConfigReg = configReg ?? ConfigReg,
            Review = review ?? Review
        };
}
