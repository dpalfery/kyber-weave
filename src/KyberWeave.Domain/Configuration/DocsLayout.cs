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

    /// <summary>
    /// The document a technology's registry property points at. A standard is one file, not
    /// a directory to be listed: an agent resolving <c>&lt;csharp-coding-standard&gt;</c>
    /// should be able to read it in one step.
    /// </summary>
    public static string TechnologyStandard(string docsRoot, string technology) =>
        $"{docsRoot}/{Standards}/{technology}/README.md";

    /// <summary>The standard's file name inside a technology, role or platform folder.</summary>
    public const string StandardsSuffix = ".standards.md";

    /// <summary>The prose rules file's name inside a technology, role or platform folder.</summary>
    public const string RulesSuffix = ".rules.md";

    /// <summary>The skills file's name inside a technology, role or platform folder.</summary>
    public const string SkillsSuffix = ".skills.md";

    /// <summary>The Arbiter's machine rules file inside a technology, role or platform folder.</summary>
    public const string ArbiterRulesSuffix = ".arbiter-rules.yml";

    /// <summary>
    /// The six `<c>##</c>` headings every standard carries, in the order it carries them.
    /// </summary>
    public static IReadOnlyList<string> StandardHeadings { get; } =
    [
        "Naming and layout",
        "Coding standards",
        "Testing",
        "Commands",
        "Security and data handling",
        "Done checklist"
    ];

    /// <summary>The standard file of a scope folder, named after the folder.</summary>
    public static string StandardFile(string docsRoot, string name) =>
        $"{docsRoot}/{Standards}/{name}/{name}{StandardsSuffix}";

    /// <summary>The prose rules file of a scope folder, named after the folder.</summary>
    public static string RulesFile(string docsRoot, string name) =>
        $"{docsRoot}/{Standards}/{name}/{name}{RulesSuffix}";

    /// <summary>The skills file of a scope folder, named after the folder.</summary>
    public static string SkillsFile(string docsRoot, string name) =>
        $"{docsRoot}/{Standards}/{name}/{name}{SkillsSuffix}";

    /// <summary>The Arbiter machine rules file of a scope folder, named after the folder.</summary>
    public static string ArbiterRulesFile(string docsRoot, string name) =>
        $"{docsRoot}/{Standards}/{name}/{name}{ArbiterRulesSuffix}";

    /// <summary>The four files a complete technology, role or platform folder holds.</summary>
    public static IReadOnlyList<string> ScopeFolderFiles(string docsRoot, string name) =>
    [
        StandardFile(docsRoot, name),
        RulesFile(docsRoot, name),
        SkillsFile(docsRoot, name),
        ArbiterRulesFile(docsRoot, name)
    ];
}
