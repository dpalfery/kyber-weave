namespace KyberWeave.Core.Configuration;

/// <summary>One property of the configuration registry.</summary>
/// <param name="Name">Lookup name, without the angle brackets it is written in.</param>
/// <param name="Path">Repository-relative path, forward-slashed.</param>
public sealed record ConfigRegEntry(string Name, string Path);

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
/// </remarks>
public sealed class ConfigRegConfig
{
    public const string DocsRootProperty = "docs-root";
    public const string DocumentationIndexProperty = "documentation-index";
    public const string DocumentationOntologyProperty = "documentation-ontology";
    public const string ComponentCatalogProperty = "component-catalog";
    public const string StandardsRootProperty = "standards-root";

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
        ("plans", "plan-index"),
        ("specs", "specification-index"),
        ("todo", "todo-index"),
        ("adr", "adr-index"),
        ("rules", "rules-index"),
        ("reference", "reference-index")
    ];

    /// <summary>Entries the host added to <c>config-reg:</c>, in the order it wrote them.</summary>
    public IReadOnlyList<ConfigRegEntry> Additions { get; init; } = [];

    public static ConfigRegConfig ProductDefaults { get; } = new();

    /// <summary>
    /// The whole registry: the derived entries for what <c>docs init</c> creates, followed by
    /// the host's additions, with an addition replacing a built-in of the same name in place.
    /// </summary>
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

        return entries;
    }
}
