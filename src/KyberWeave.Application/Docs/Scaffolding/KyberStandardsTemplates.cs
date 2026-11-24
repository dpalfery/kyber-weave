using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// The folders the Kyber Squad coding standards templates ship in, and the file name each part
/// carries. Pure: the template text itself is read through <see cref="IKyberStandardsTemplates"/>.
/// </summary>
/// <remarks>
/// These rich templates are authored under <c>products/kyber-squad/standards/</c> and embedded as
/// resources at build time so that single-file distributions of the CLI and MCP server can
/// scaffold complete standards without depending on runtime filesystem paths.
/// </remarks>
public static class KyberStandardsTemplates
{
    internal const string ResourcePrefix = "Standards.";

    /// <summary>The parts a shipped technology, role or platform folder holds.</summary>
    public enum Part
    {
        /// <summary>The standard, six headings.</summary>
        Standard,

        /// <summary>Prose rules a model reads.</summary>
        Rules,

        /// <summary>The YAML list of KW skill names the scope gets; a list, never prose.</summary>
        Skills,

        /// <summary>Machine rules the Arbiter evaluates; may declare none.</summary>
        ArbiterRules
    }

    private static readonly string[] CanonicalTechnologies =
    [
        "aws",
        "azure",
        "csharp",
        "data-access-layer",
        "github-actions",
        "go",
        "maui",
        "pulumi",
        "python",
        "react",
        "rust",
        "sql-server",
        "tauri",
        "typescript"
    ];

    /// <summary>Role templates: the same four files, scoped by <c>role</c>.</summary>
    private static readonly string[] CanonicalRoles =
        ["architect", "conductor", "devops", "reviewer"];

    /// <summary>Delivery-platform templates: technology folders, scoped by <c>technology</c>.</summary>
    private static readonly string[] CanonicalPlatforms =
        ["azure-devops", "github", "gitlab", "jira"];

    /// <summary>
    /// Gets all canonical technology slugs with an embedded standard template.
    /// </summary>
    public static IReadOnlyList<string> All => CanonicalTechnologies;

    /// <summary>The role folder templates that ship.</summary>
    public static IReadOnlyList<string> Roles => CanonicalRoles;

    /// <summary>The delivery-platform folder templates that ship.</summary>
    public static IReadOnlyList<string> Platforms => CanonicalPlatforms;

    /// <summary>Every folder template: technologies, roles and platforms.</summary>
    public static IReadOnlyList<string> AllFolders =>
        [.. CanonicalTechnologies, .. CanonicalRoles, .. CanonicalPlatforms];

    /// <summary>The frontmatter key a folder's files are scoped by.</summary>
    public static string ScopeKey(string name) =>
        CanonicalRoles.Contains(name, StringComparer.Ordinal) ? "role" : "technology";

    /// <summary>The file suffix each part carries in a folder named after its scope.</summary>
    public static string FileName(string scope, Part part) => part switch
    {
        Part.Standard => $"{scope}.standards.md",
        Part.Rules => $"{scope}.rules.md",
        Part.Skills => $"{scope}{DocsLayout.SkillsListSuffix}",
        Part.ArbiterRules => $"{scope}.arbiter-rules.yml",
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };

    /// <summary>The manifest resource name one file of a folder is embedded under.</summary>
    internal static string ResourceName(string technology, Part part) => part switch
    {
        Part.Standard => $"{ResourcePrefix}{technology}.standards.md",
        Part.Rules => $"{ResourcePrefix}{technology}.rules.md",
        Part.Skills => $"{ResourcePrefix}{technology}{DocsLayout.SkillsListSuffix}",
        Part.ArbiterRules => $"{ResourcePrefix}{technology}.arbiter-rules.yml",
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
}
