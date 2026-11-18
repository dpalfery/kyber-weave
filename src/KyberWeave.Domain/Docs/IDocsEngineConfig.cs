namespace KyberWeave.Core.Configuration;

/// <summary>The slice of the host configuration the documentation engine reads.</summary>
/// <remarks>
/// The engine's validators, plan lifecycle and delivery code read only these four sections. Naming the slice lets that
/// code live below the full configuration type, which carries sections the engine never touches.
/// </remarks>
public interface IDocsEngineConfig
{
    /// <summary>Gets the documentation ontology: roots, catalog and intake.</summary>
    OntologyConfig Ontology { get; }

    /// <summary>Gets the config-registration section: the files the host registers and how they resolve.</summary>
    ConfigRegConfig ConfigReg { get; }

    /// <summary>Gets the review section: task gates and the test map.</summary>
    ReviewConfig Review { get; }

    /// <summary>Gets the documentation-analysis section, including the managed glossary path.</summary>
    DocsAnalysisConfig DocsAnalysis { get; }

    /// <summary>Gets the worker roster this repository dispatches from: <c>technology</c> or <c>tiered</c>.</summary>
    string WorkerRoster { get; }
}
