using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using KyberWeave.Contracts.Host;
using KyberWeave.Core.Docs.KwSkills;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>The Kyber standards templates embedded in the assembly the resource seam names.</summary>
/// <remarks>
/// <para>
/// The templates are read once, on the first lookup, over the <see cref="IEmbeddedResources"/> seam, so a unit test
/// supplies the text and a host reads what its build ships. A resource the build does not ship reads as no template
/// rather than as a failure: most folders ship three files and declare no Arbiter rules.
/// </para>
/// <para>
/// When rendering a template for a host repository, the frontmatter <c>owner</c> and <c>last-reviewed</c> date are
/// replaced and the remainder of the template is left intact. Both values are YAML-encoded with the same scalar
/// quoting <c>DocsScaffolder</c> uses, so punctuation in an accepted owner (colons, hashes, quotes) stays a scalar
/// rather than becoming a nested mapping or a comment.
/// </para>
/// </remarks>
public sealed class KyberStandardsTemplateReader : IKyberStandardsTemplates
{
    private readonly IEmbeddedResources _resources;
    private readonly ShippedKwSkillPool _skillPool;
    private readonly Lazy<FrozenDictionary<string, string>> _templates;

    /// <summary>Creates the reader over the resource seam it reads.</summary>
    /// <param name="resources">Reads the manifest resources the templates are embedded as.</param>
    public KyberStandardsTemplateReader(IEmbeddedResources resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        _resources = resources;
        _skillPool = new ShippedKwSkillPool(resources);
        _templates = new Lazy<FrozenDictionary<string, string>>(LoadTemplates);
    }

    /// <inheritdoc />
    public bool TryGet(string? technology, [NotNullWhen(true)] out string? content) =>
        TryGetPart(technology, KyberStandardsTemplates.Part.Standard, out content);

    /// <inheritdoc />
    public bool TryGetPart(
        string? technology,
        KyberStandardsTemplates.Part part,
        [NotNullWhen(true)] out string? content)
    {
        if (technology is null)
        {
            content = null;
            return false;
        }

        return _templates.Value.TryGetValue(KyberStandardsTemplates.ResourceName(technology, part), out content);
    }

    /// <inheritdoc />
    public bool TryRender(
        string? technology,
        string owner,
        string lastReviewed,
        [NotNullWhen(true)] out string? rendered)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lastReviewed);

        if (technology is not null && TryGet(technology, out string? content))
        {
            rendered = InjectFrontmatter(content, owner, lastReviewed);
            return true;
        }

        rendered = null;
        return false;
    }

    /// <inheritdoc />
    public string Render(string technology, string owner, string lastReviewed)
    {
        ArgumentNullException.ThrowIfNull(technology);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lastReviewed);

        if (!TryRender(technology, owner, lastReviewed, out string? rendered))
        {
            throw new ArgumentException(UnknownFolderMessage(technology), nameof(technology));
        }

        return rendered;
    }

    /// <inheritdoc />
    public bool TryRenderPart(
        string? technology,
        KyberStandardsTemplates.Part part,
        string owner,
        string lastReviewed,
        [NotNullWhen(true)] out string? rendered)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lastReviewed);

        if (technology is not null && TryGetPart(technology, part, out string? content))
        {
            // A machine file (the Arbiter rules, the skills list) carries no frontmatter to inject into.
            rendered = part is KyberStandardsTemplates.Part.ArbiterRules or KyberStandardsTemplates.Part.Skills
                ? content
                : InjectFrontmatter(content, owner, lastReviewed);
            return true;
        }

        rendered = null;
        return false;
    }

    /// <inheritdoc />
    public string RenderPart(
        string technology,
        KyberStandardsTemplates.Part part,
        string owner,
        string lastReviewed)
    {
        ArgumentNullException.ThrowIfNull(technology);

        if (!TryRenderPart(technology, part, owner, lastReviewed, out string? rendered))
        {
            throw new ArgumentException(UnknownFolderMessage(technology), nameof(technology));
        }

        return rendered;
    }

    /// <inheritdoc />
    public bool TryRenderSharedRules(string owner, string lastReviewed, [NotNullWhen(true)] out string? rendered)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(lastReviewed);

        if (TryRead($"{KyberStandardsTemplates.ResourcePrefix}shared-rules.md", out string? sharedRules))
        {
            rendered = InjectFrontmatter(sharedRules, owner, lastReviewed);
            return true;
        }

        rendered = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryReadShippedSkill(string name, out IReadOnlyList<ShippedSkillFile> files)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!_skillPool.Contains(name))
        {
            files = [];
            return false;
        }

        List<ShippedSkillFile> read = [];
        foreach (string relative in _skillPool.FilesOf(name))
        {
            using Stream stream = _skillPool.Open(name, relative);
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            read.Add(new ShippedSkillFile(relative, copy.ToArray()));
        }

        files = read;
        return true;
    }

    /// <summary>The unknown-folder message, with a nearest-match hint when one is computable.</summary>
    private static string UnknownFolderMessage(string technology)
    {
        string? nearest = StringDistance.Nearest(technology, KyberStandardsTemplates.AllFolders);
        return nearest is null
            ? $"Unknown technology '{technology}'."
            : $"Unknown technology '{technology}'. Did you mean '{nearest}'?";
    }

    private static string InjectFrontmatter(string template, string owner, string lastReviewed)
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
                lines[i] = "last-reviewed: " + HostConfigYaml.QuoteScalar(lastReviewed) + (hasCr ? "\r" : string.Empty);
            }
        }

        return string.Join('\n', lines);
    }

    private FrozenDictionary<string, string> LoadTemplates()
    {
        Dictionary<string, string> templates = new(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in KyberStandardsTemplates.AllFolders)
        {
            foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
            {
                string resourceName = KyberStandardsTemplates.ResourceName(folder, part);
                if (TryRead(resourceName, out string? content))
                {
                    templates[resourceName] = content;
                }
            }
        }

        return templates.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads one resource, reporting one the build does not ship as absent rather than as a failure.</summary>
    private bool TryRead(string resourceName, [NotNullWhen(true)] out string? text)
    {
        try
        {
            text = _resources.ReadAllText(resourceName);
            return true;
        }
        catch (MissingEmbeddedResourceException)
        {
            text = null;
            return false;
        }
    }
}
