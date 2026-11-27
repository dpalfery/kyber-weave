namespace KyberWeave.Core.Configuration;

/// <summary>Resolves a <see cref="ConfigRegConfig"/> and an <see cref="OntologyConfig"/> into the registry entries.</summary>
public static class ConfigRegConfigResolution
{
    /// <summary>
    /// Registry property per scaffolded folder. Each resolves to that folder's canonical
    /// README rather than to the directory, except a technology's standard, which resolves
    /// to its own prefixed file: a property an agent can read in one step is worth more than
    /// one it has to list, and the README is the folder's own index.
    /// <c>standards</c> is the exception, published as a root because the deferred standards
    /// skill enumerates it.
    /// </summary>
    private static readonly (string Folder, string Property)[] FolderProperties =
    [
        (DocsLayout.Plans, ConfigRegConfig.PlanIndexProperty),
        (DocsLayout.Specifications, ConfigRegConfig.SpecificationIndexProperty),
        (DocsLayout.Todos, ConfigRegConfig.TodoIndexProperty),
        (DocsLayout.Adr, "adr-index"),
        (DocsLayout.Rules, ConfigRegConfig.RulesIndexProperty),
        (DocsLayout.Reference, "reference-index")
    ];

    /// <summary>
    /// The whole registry: the derived entries for what <c>docs init</c> creates, followed by
    /// the host's additions, with an addition replacing a built-in of the same name in place,
    /// then any mode properties the configuration publishes.
    /// </summary>
    /// <remarks>
    /// Mode properties come last, beside the <c>&lt;autonomy&gt;</c> line the renderer appends,
    /// so the generated block reads as one list of paths and then the settings that describe
    /// the repository. A host cannot shadow one with an addition: the configuration loader
    /// refuses a reserved name under <c>config-reg:</c>, which is what keeps the append here
    /// from producing a second entry of a name already in the list.
    /// </remarks>
    public static IReadOnlyList<ConfigRegEntry> Resolve(this ConfigRegConfig config, OntologyConfig ontology)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ontology);

        string root = ontology.DocsRoot;
        List<ConfigRegEntry> entries =
        [
            new(ConfigRegConfig.DocsRootProperty, root),
            new(ConfigRegConfig.DocumentationIndexProperty, DocsLayout.Index(root)),
            new(ConfigRegConfig.DocumentationOntologyProperty, DocsLayout.Ontology(root)),
            new(ConfigRegConfig.ComponentCatalogProperty, ontology.ResolvedCatalogPath),
            new(ConfigRegConfig.StandardsRootProperty, DocsLayout.Folder(root, DocsLayout.Standards))
        ];

        foreach (string technology in ontology.Technologies)
        {
            entries.Add(new ConfigRegEntry(
                technology + ConfigRegConfig.CodingStandardSuffix,
                DocsLayout.TechnologyStandard(root, technology)));
        }

        foreach ((string folder, string property) in FolderProperties)
        {
            entries.Add(new ConfigRegEntry(property, DocsLayout.FolderIndex(root, folder)));
        }

        foreach (ConfigRegEntry addition in config.Additions)
        {
            int existing = entries.FindIndex(e => string.Equals(e.Name, addition.Name, StringComparison.Ordinal));
            if (existing >= 0)
                entries[existing] = addition;
            else
                entries.Add(addition);
        }

        // Opt-in only: publishing 'enabled' for a repository that never set the flag would
        // claim a behaviour it did not ask for, and the skills reading the registry would run
        // an assessment nobody wanted.
        if (ontology.Intake)
        {
            entries.Add(new ConfigRegEntry(
                ConfigRegConfig.IntakeAssessmentProperty,
                ConfigRegConfig.IntakeAssessmentEnabled,
                ConfigRegEntryKind.Mode));
        }

        // Same rule for the run settings: a property an operator did not write publishes
        // nothing, because an agent already applies the documented default. Only a value that
        // was actually chosen is worth a line, and only a chosen value can differ from it.
        // stop-on-tool-failure is deliberately not published: the CLI and the MCP server are
        // the readers, not an agent instruction file (ADR 0061).
        if (!string.IsNullOrWhiteSpace(config.WorkerRoster))
        {
            entries.Add(new ConfigRegEntry(
                ConfigRegConfig.WorkerRosterProperty,
                config.WorkerRoster,
                ConfigRegEntryKind.Mode));
        }

        if (!string.IsNullOrWhiteSpace(config.RunBaseBranch))
        {
            entries.Add(new ConfigRegEntry(
                ConfigRegConfig.RunBaseBranchProperty,
                config.RunBaseBranch,
                ConfigRegEntryKind.Mode));
        }

        // The onboarding answer is published for the same reason autonomy is: a run-start
        // check a worker cannot read from its instructions is one it cannot honour. An
        // unanswered repository publishes nothing, so no reader mistakes silence for 'done'.
        if (!string.IsNullOrWhiteSpace(config.Onboarding))
        {
            entries.Add(new ConfigRegEntry(
                ConfigRegConfig.OnboardingProperty,
                config.Onboarding,
                ConfigRegEntryKind.Mode));
        }

        return entries;
    }

    /// <summary>
    /// A boolean as the registry spells it. Deliberately not <c>bool.ToString()</c>, which
    /// capitalises: the rendered block is read by agents comparing against <c>true</c> and
    /// <c>false</c>, not against the framework's formatting.
    /// </summary>
    private static string ModeWord(bool value)
    {
        return value ? "true" : "false";
    }
}
