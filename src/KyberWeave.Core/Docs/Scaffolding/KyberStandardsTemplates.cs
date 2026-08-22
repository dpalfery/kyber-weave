using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using KyberWeave.Core.Docs.Validation;

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
/// <see cref="DocsScaffolder"/> uses, so punctuation in an accepted owner (colons, hashes,
/// quotes) stays a scalar rather than becoming a nested mapping or a comment.
/// </para>
/// </remarks>
public static class KyberStandardsTemplates
{
    private const string ResourcePrefix = "Standards.";
    private const string ResourceSuffix = ".md";

    private static readonly string[] CanonicalTechnologies =
    [
        "azure",
        "csharp",
        "data-access-layer",
        "github-actions",
        "maui",
        "pulumi",
        "python",
        "react",
        "sql",
        "test"
    ];

    private static readonly FrozenDictionary<string, string> Templates = LoadEmbeddedTemplates();

    /// <summary>
    /// Gets all canonical technology slugs with an embedded standard template.
    /// </summary>
    public static IReadOnlyList<string> All => CanonicalTechnologies;

    /// <summary>
    /// Attempts to retrieve the raw embedded template markdown for the given technology.
    /// </summary>
    /// <param name="technology">The technology slug (e.g. <c>csharp</c>, <c>test</c>).</param>
    /// <param name="template">The raw template content if found; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a template exists for the technology; otherwise, <see langword="false"/>.</returns>
    public static bool TryGet(string? technology, [NotNullWhen(true)] out string? template)
    {
        if (technology is null)
        {
            template = null;
            return false;
        }

        return Templates.TryGetValue(technology, out template);
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
            string? nearest = DocSpecValidator.Nearest(technology, CanonicalTechnologies);
            string message = nearest is not null
                ? $"Unknown technology '{technology}'. Did you mean '{nearest}'?"
                : $"Unknown technology '{technology}'.";

            throw new ArgumentException(message, nameof(technology));
        }

        return rendered;
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

        foreach (string tech in CanonicalTechnologies)
        {
            string resourceName = $"{ResourcePrefix}{tech}{ResourceSuffix}";
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
            dictionary[tech] = reader.ReadToEnd();
        }

        return dictionary.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
