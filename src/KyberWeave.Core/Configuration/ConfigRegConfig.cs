namespace KyberWeave.Core.Configuration;

/// <summary>What the value of a registry entry means.</summary>
/// <remarks>
/// The distinction exists because the registry's guarantee is "every path value resolves",
/// and a setting is not a path. Carrying the kind on the entry keeps that guarantee intact
/// for paths: a validator can skip the one kind whose value is not meant to be found on
/// disk, instead of learning which property names are special.
/// </remarks>
public enum ConfigRegEntryKind
{
    /// <summary>A repository-relative path that must resolve to a file or directory.</summary>
    Path,

    /// <summary>A mode word describing configuration state; nothing on disk answers to it.</summary>
    Mode
}

/// <summary>One property of the configuration registry.</summary>
/// <param name="Name">Lookup name, without the angle brackets it is written in.</param>
/// <param name="Path">
/// For <see cref="ConfigRegEntryKind.Path"/>, the repository-relative path, forward-slashed.
/// For <see cref="ConfigRegEntryKind.Mode"/>, the mode word the property publishes — the
/// field is named for its usual occupant and is not a path in that case.
/// </param>
/// <param name="Kind">Whether <paramref name="Path"/> is a path or a mode word.</param>
public sealed record ConfigRegEntry(
    string Name,
    string Path,
    ConfigRegEntryKind Kind = ConfigRegEntryKind.Path);

/// <summary>
/// The repository's configuration registry: named paths that portable agents and skills look
/// up instead of embedding a relative path out of their own directory.
/// </summary>
/// <remarks>
/// <para>
/// Only the host's own additions are stored. Everything <c>docs init</c> creates is
/// <em>derived</em> from the documentation root and the declared technologies rather than
/// written into the configuration file — a stored copy of a derivable path is a path that
/// goes stale the day <c>docs-root</c> changes, and the operator would then be repairing
/// values they never authored.
/// </para>
/// <para>
/// An addition may reuse a built-in name, which replaces that entry in place. A repository
/// that keeps its ADRs somewhere else gets to say so; what it may not do is remove the name,
/// because a skill written against the registry resolves properties by name and a missing one
/// is indistinguishable from a typo.
/// </para>
/// <para>
/// A reserved name is the exception to that replacement: a <em>mode</em> property carries a
/// setting rather than a path, so declaring one under <c>config-reg:</c> is refused rather
/// than allowed to replace the derived entry with a path. The registry's promise survives —
/// every path value resolves — because only the entry kind that is not a path is exempt.
/// </para>
/// </remarks>
public sealed class ConfigRegConfig
{
    public const string DocsRootProperty = "docs-root";
    public const string DocumentationIndexProperty = "documentation-index";
    public const string DocumentationOntologyProperty = "documentation-ontology";
    public const string ComponentCatalogProperty = "component-catalog";
    public const string StandardsRootProperty = "standards-root";
    public const string PlanIndexProperty = "plan-index";
    public const string TodoIndexProperty = "todo-index";
    public const string SpecificationIndexProperty = "specification-index";

    /// <summary>
    /// Registry property carrying the repository's autonomy setting. Reserved: it holds a mode
    /// word, not a path, so a host may not declare it under <c>config-reg:</c>.
    /// </summary>
    public const string AutonomyProperty = "autonomy";

    public const string SupervisedAutonomy = "supervised";
    public const string AutonomousAutonomy = "autonomous";

    /// <summary>
    /// Registry property reporting whether this repository opted in to the intake assessment.
    /// A <see cref="ConfigRegEntryKind.Mode"/> entry, published from <c>ontology.intake:</c>
    /// and reserved: a host may not declare it under <c>config-reg:</c>.
    /// </summary>
    public const string IntakeAssessmentProperty = "intake-assessment";

    /// <summary>
    /// The value <see cref="IntakeAssessmentProperty"/> carries when the assessment is on.
    /// Absence of the property is the off state, so there is no word for it: a skill that
    /// finds no property skips the assessment, and one that finds <em>a</em> value runs it.
    /// </summary>
    public const string IntakeAssessmentEnabled = "enabled";

    /// <summary>Suffix that turns a declared technology into its registry property name.</summary>
    public const string CodingStandardSuffix = "-coding-standard";

    /// <summary>
    /// Registry property per scaffolded folder. Each resolves to that folder's canonical
    /// README rather than to the directory: a property an agent can read in one step is
    /// worth more than one it has to list, and the README is the folder's own index.
    /// <c>standards</c> is the exception, published as a root because the deferred standards
    /// skill enumerates it.
    /// </summary>
    private static readonly (string Folder, string Property)[] FolderProperties =
    [
        ("plans", PlanIndexProperty),
        ("specs", SpecificationIndexProperty),
        ("todo", TodoIndexProperty),
        ("adr", "adr-index"),
        ("rules", "rules-index"),
        ("reference", "reference-index")
    ];

    /// <summary>Entries the host added to <c>config-reg:</c>, in the order it wrote them.</summary>
    public IReadOnlyList<ConfigRegEntry> Additions { get; init; } = [];

    /// <summary>
    /// The top-level <c>autonomy:</c> setting — <see cref="SupervisedAutonomy"/> or
    /// <see cref="AutonomousAutonomy"/> — or null when the repository did not set one, which
    /// agents read as supervised. Rendered into the Config Reg block only when set, and never
    /// resolved as a path.
    /// </summary>
    public string? Autonomy { get; init; }

    public static ConfigRegConfig ProductDefaults { get; } = new();

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
    public IReadOnlyList<ConfigRegEntry> Resolve(OntologyConfig ontology)
    {
        ArgumentNullException.ThrowIfNull(ontology);

        string root = ontology.DocsRoot;
        List<ConfigRegEntry> entries =
        [
            new ConfigRegEntry(DocsRootProperty, root),
            new ConfigRegEntry(DocumentationIndexProperty, DocsLayout.Index(root)),
            new ConfigRegEntry(DocumentationOntologyProperty, DocsLayout.Ontology(root)),
            new ConfigRegEntry(ComponentCatalogProperty, ontology.ResolvedCatalogPath),
            new ConfigRegEntry(StandardsRootProperty, DocsLayout.Folder(root, DocsLayout.Standards))
        ];

        foreach (string technology in ontology.Technologies)
        {
            entries.Add(new ConfigRegEntry(
                technology + CodingStandardSuffix,
                DocsLayout.TechnologyStandard(root, technology)));
        }

        foreach ((string folder, string property) in FolderProperties)
        {
            entries.Add(new ConfigRegEntry(property, DocsLayout.FolderIndex(root, folder)));
        }

        foreach (ConfigRegEntry addition in Additions)
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
                IntakeAssessmentProperty,
                IntakeAssessmentEnabled,
                ConfigRegEntryKind.Mode));
        }

        return entries;
    }
}
