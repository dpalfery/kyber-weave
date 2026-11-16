using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// Provides access to the canonical Kyber Squad coding standards templates embedded in
/// the assembly.
/// </summary>
/// <remarks>
/// <para>
/// These rich templates are authored under <c>products/kyber-squad/standards/</c> and
/// embedded as resources at build time so that single-file distributions of the CLI and
/// MCP server can scaffold complete standards without depending on runtime filesystem paths.
/// </para>
/// <para>
/// When rendering a template for a host repository, <see cref="Render"/> replaces the default
/// frontmatter <c>owner</c> and <c>last-reviewed</c> date while leaving the remainder of the
/// template intact. Both values are YAML-encoded with the same scalar quoting
/// <c>DocsScaffolder</c> uses, so punctuation in an accepted owner (colons, hashes,
/// quotes) stays a scalar rather than becoming a nested mapping or a comment.
/// </para>
/// </remarks>
public static class KyberStandardsTemplates
{
    private const string ResourcePrefix = "Standards.";

    /// <summary>The parts a shipped technology, role or platform folder holds.</summary>
    public enum Part
    {
        /// <summary>The standard, six headings.</summary>
        Standard,

        /// <summary>Prose rules a model reads.</summary>
        Rules,

        /// <summary>How to do the work.</summary>
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
        "sql",
        "sql-server",
        "tauri",
        "test",
        "typescript"
    ];

    /// <summary>Role templates: the same four files, scoped by <c>role</c>.</summary>
    private static readonly string[] CanonicalRoles =
        ["architect", "conductor", "devops", "reviewer"];

    /// <summary>Delivery-platform templates: technology folders, scoped by <c>technology</c>.</summary>
    private static readonly string[] CanonicalPlatforms =
        ["azure-devops", "github", "gitlab", "jira"];

    private static readonly FrozenDictionary<string, string> Templates = LoadEmbeddedTemplates();

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
        Part.Skills => $"{scope}.skills.md",
        Part.ArbiterRules => $"{scope}.arbiter-rules.yml",
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };

    /// <summary>
    /// Attempts to retrieve the raw embedded template markdown for the given technology.
    /// </summary>
    /// <param name="technology">The technology slug (e.g. <c>csharp</c>, <c>test</c>).</param>
    /// <param name="template">The raw template content if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a template exists for the technology; otherwise, <see langword="false"/>.</returns>
    public static bool TryGet(string? technology, [NotNullWhen(true)] out string? template) =>
        TryGetPart(technology, Part.Standard, out template);

    /// <summary>Attempts to retrieve one file of a technology's embedded template.</summary>
    public static bool TryGetPart(
        string? technology,
        Part part,
        [NotNullWhen(true)] out string? template)
    {
        if (technology is null)
        {
            template = null;
            return false;
        }

        return Templates.TryGetValue(ResourceName(technology, part), out template);
    }

    /// <summary>
    /// Attempts to render the coding standard template for <paramref name="technology"/>, populating
    /// the frontmatter <c>owner</c> and <c>last-reviewed</c> fields.
    /// </summary>
    /// <param name="technology">The technology slug.</param>
    /// <param name="owner">The owner value to inject into frontmatter.</param>
    /// <param name="date">The last-reviewed ISO date (<c>yyyy-MM-dd</c>) to inject.</param>
    /// <param name="rendered">The rendered template content if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a template exists for the technology and was rendered; otherwise, <see langword="false"/>.</returns>
    public static bool TryRender(
        string? technology,
        string owner,
        string date,
        [NotNullWhen(true)] out string? rendered)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(date);

        if (technology is not null && TryGet(technology, out string? template))
        {
            rendered = InjectFrontmatter(template, owner, date);
            return true;
        }

        rendered = null;
        return false;
    }

    /// <summary>
    /// Renders the coding standard template for <paramref name="technology"/>, populating
    /// the frontmatter <c>owner</c> and <c>last-reviewed</c> fields.
    /// </summary>
    /// <param name="technology">The technology slug.</param>
    /// <param name="owner">The owner value to inject into frontmatter.</param>
    /// <param name="date">The last-reviewed ISO date (<c>yyyy-MM-dd</c>) to inject.</param>
    /// <returns>The rendered template content.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="technology"/> is not recognised.</exception>
    public static string Render(string technology, string owner, string date)
    {
        ArgumentNullException.ThrowIfNull(technology);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(date);

        if (!TryRender(technology, owner, date, out string? rendered))
        {
            string? nearest = StringDistance.Nearest(technology, AllFolders);
            string message = nearest is not null
                ? $"Unknown technology '{technology}'. Did you mean '{nearest}'?"
                : $"Unknown technology '{technology}'.";

            throw new ArgumentException(message, nameof(technology));
        }

        return rendered;
    }

    /// <summary>
    /// Attempts to render one file of a technology template, populating the frontmatter
    /// <c>owner</c> and <c>last-reviewed</c> fields. The Arbiter rules file has no
    /// frontmatter and is returned unchanged.
    /// </summary>
    public static bool TryRenderPart(
        string? technology,
        Part part,
        string owner,
        string date,
        [NotNullWhen(true)] out string? rendered)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(date);

        if (technology is not null && TryGetPart(technology, part, out string? template))
        {
            rendered = part == Part.ArbiterRules
                ? template
                : InjectFrontmatter(template, owner, date);
            return true;
        }

        rendered = null;
        return false;
    }

    /// <summary>Renders one file of a technology template, or throws naming the nearest slug.</summary>
    public static string RenderPart(string technology, Part part, string owner, string date)
    {
        ArgumentNullException.ThrowIfNull(technology);

        if (!TryRenderPart(technology, part, owner, date, out string? rendered))
        {
            string? nearest = StringDistance.Nearest(technology, AllFolders);
            string message = nearest is not null
                ? $"Unknown technology '{technology}'. Did you mean '{nearest}'?"
                : $"Unknown technology '{technology}'.";

            throw new ArgumentException(message, nameof(technology));
        }

        return rendered;
    }

    /// <summary>
    /// Attempts to render the shared rules file that sits beside a host's standards folders.
    /// </summary>
    public static bool TryRenderSharedRules(
        string owner,
        string date,
        [NotNullWhen(true)] out string? rendered)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(date);

        Assembly assembly = typeof(KyberStandardsTemplates).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream($"{ResourcePrefix}shared-rules.md");
        if (stream is null)
        {
            rendered = null;
            return false;
        }

        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        rendered = InjectFrontmatter(reader.ReadToEnd(), owner, date);
        return true;
    }

    private static string InjectFrontmatter(string template, string owner, string date)
    {
        string[] lines = template.Split('\n');
        bool inFrontmatter = false;
        int frontmatterCount = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimEnd('\r');
            if (trimmed == "---")
            {
                frontmatterCount++;
                if (frontmatterCount == 1)
                {
                    inFrontmatter = true;
                    continue;
                }

                if (frontmatterCount == 2)
                {
                    break;
                }
            }

            if (!inFrontmatter)
            {
                continue;
            }

            bool hasCr = lines[i].EndsWith('\r');
            if (trimmed.StartsWith("owner:", StringComparison.Ordinal))
            {
                lines[i] = "owner: " + HostConfigYaml.QuoteScalar(owner) + (hasCr ? "\r" : string.Empty);
            }
            else if (trimmed.StartsWith("last-reviewed:", StringComparison.Ordinal))
            {
                lines[i] = "last-reviewed: " + HostConfigYaml.QuoteScalar(date) + (hasCr ? "\r" : string.Empty);
            }
        }

        return string.Join('\n', lines);
    }

    private static FrozenDictionary<string, string> LoadEmbeddedTemplates()
    {
        Assembly assembly = typeof(KyberStandardsTemplates).Assembly;
        Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string tech in AllFolders)
        {
            foreach (Part part in Enum.GetValues<Part>())
            {
                string resourceName = ResourceName(tech, part);
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                {
                    continue;
                }

                using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
                dictionary[resourceName] = reader.ReadToEnd();
            }
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResourceName(string technology, Part part) => part switch
    {
        Part.Standard => $"{ResourcePrefix}{technology}.standards.md",
        Part.Rules => $"{ResourcePrefix}{technology}.rules.md",
        Part.Skills => $"{ResourcePrefix}{technology}.skills.md",
        Part.ArbiterRules => $"{ResourcePrefix}{technology}.arbiter-rules.yml",
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };
}
