namespace KyberWeave.Core.Configuration;

/// <summary>
/// The directory structure <c>docs init</c> creates under a documentation root, and the
/// paths derived from it.
/// </summary>
/// <remarks>
/// One definition, because three things must agree about it and each of them is written
/// somewhere else: the scaffolder creates these directories, the configuration registry
/// publishes their paths under stable property names, and validation resolves those paths
/// back to disk. A layout spelled out separately in each place is a layout that drifts the
/// first time a folder is renamed.
/// </remarks>
public static class DocsLayout
{
    /// <summary>Holds a technology's coding standard per subdirectory.</summary>
    public const string Standards = "standards";

    /// <summary>
    /// The directories every governed corpus gets, each with a canonical index README.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>archive/</c>, which is created by the first archival and is
    /// excluded from the corpus anyway, and not <c>devops/</c>, which is one repository's
    /// convention rather than a shape the ontology has an opinion about.
    /// </remarks>
    public static IReadOnlyList<string> Folders { get; } =
        [Standards, "plans", "specs", "todo", "adr", "rules", "reference"];

    public static string Index(string docsRoot) => $"{docsRoot}/README.md";

    public static string Ontology(string docsRoot) => $"{docsRoot}/documentation-ontology.md";

    public static string Folder(string docsRoot, string folder) => $"{docsRoot}/{folder}";

    public static string FolderIndex(string docsRoot, string folder) => $"{docsRoot}/{folder}/README.md";

    public static string TechnologyFolder(string docsRoot, string technology) =>
        $"{docsRoot}/{Standards}/{technology}";

    /// <summary>
    /// The document a technology's registry property points at. A standard is one file, not
    /// a directory to be listed: an agent resolving <c>&lt;csharp-coding-standard&gt;</c>
    /// should be able to read it in one step.
    /// </summary>
    public static string TechnologyStandard(string docsRoot, string technology) =>
        $"{docsRoot}/{Standards}/{technology}/README.md";
}
