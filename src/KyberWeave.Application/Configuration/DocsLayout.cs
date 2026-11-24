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
        StandardFile(docsRoot, technology);

    /// <summary>The standard's file name inside a technology, role or platform folder.</summary>
    public const string StandardsSuffix = ".standards.md";

    /// <summary>
    /// The one command that renames a legacy <c>README.md</c> standard and reshapes its
    /// sections under the six headings. Named here because validators in one layer and the
    /// migration in another both point an operator at it.
    /// </summary>
    public const string MigrateStandardsCommand = "kyber-weave docs standards migrate";

    /// <summary>The prose rules file's name inside a technology, role or platform folder.</summary>
    public const string RulesSuffix = ".rules.md";

    /// <summary>
    /// The legacy skills file's name inside a technology, role or platform folder: prose that carries
    /// skill guidance. The KW skill catalog does not read it; it reads <see cref="SkillsListSuffix"/>.
    /// </summary>
    public const string SkillsSuffix = ".skills.md";

    /// <summary>
    /// The name of the file that lists a scope's KW skills: a YAML sequence of skill names and nothing
    /// else. <see cref="SkillsSuffix"/> is the legacy prose file of the same scope.
    /// </summary>
    public const string SkillsListSuffix = ".skills.yml";

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

    /// <summary>The legacy prose skills file of a scope folder, named after the folder.</summary>
    public static string SkillsFile(string docsRoot, string name) =>
        $"{docsRoot}/{Standards}/{name}/{name}{SkillsSuffix}";

    /// <summary>The file listing a scope folder's KW skills, named after the folder.</summary>
    public static string SkillsListFile(string docsRoot, string name) =>
        $"{docsRoot}/{Standards}/{name}/{name}{SkillsListSuffix}";

    /// <summary>
    /// The folder holding the repository's own KW skills, one subfolder per skill with a
    /// <c>SKILL.md</c> inside. The documentation corpus skips it: a skill is not a governed document.
    /// </summary>
    public static string SkillsPool(string docsRoot) => $"{docsRoot}/{Standards}/skills";

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

    /// <summary>
    /// The frontmatter key that scopes a folder's files: <c>role</c> for a declared role, and
    /// <c>technology</c> for everything else. One decision, because the stub the migration writes
    /// and the check the validator makes must agree about which line a folder's files carry.
    /// </summary>
    /// <param name="roles">The roles the host declares.</param>
    /// <param name="scope">The folder's name.</param>
    public static string ScopeKey(IReadOnlyList<string> roles, string scope) =>
        roles.Contains(scope, StringComparer.Ordinal) ? "role" : "technology";
}
