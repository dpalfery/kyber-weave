using KyberWeave.Core.Configuration;
using KyberWeave.Core.Parsing;
using KyberWeave.Core.Review;
using YamlDotNet.Core;

namespace KyberWeave.Application.Review;

/// <summary>
/// Finds the plan of the change under review among the change's own Markdown files, so a seat can judge each
/// finding against the request's acceptance criteria and the plan's approved decisions.
/// </summary>
/// <remarks>
/// The lookup reads the changed-file list rather than the plan lifecycle: closeout marks a plan Completed before the
/// review cycle runs, so a search for Ready or In-progress plans would miss the plan the change implements.
/// </remarks>
internal static class ChangePlanLookup
{
    private const string Markdown = ".md";
    private const string PlanDocType = "plan";
    private const string IndexFileName = "README.md";

    /// <summary>The host's plan folder, repository-relative, or null when the configuration registers no plan index.</summary>
    /// <param name="config">The host's effective configuration.</param>
    internal static string? PlanFolder(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? index = config.ConfigReg.Resolve(config.Ontology)
            .FirstOrDefault(entry => string.Equals(entry.Name, ConfigRegConfig.PlanIndexProperty, StringComparison.Ordinal))
            ?.Path;
        if (string.IsNullOrWhiteSpace(index))
            return null;

        string normalized = index.Replace('\\', '/');
        int separator = normalized.LastIndexOf('/');
        return separator > 0 ? normalized[..separator] : null;
    }

    /// <summary>
    /// The plans the change touches: the changed Markdown files directly under <paramref name="planFolder"/> whose
    /// frontmatter declares <c>doc-type: plan</c>, in ordinal order.
    /// </summary>
    /// <param name="changed">The whole change's paths, repository-relative; null when git could not name them.</param>
    /// <param name="planFolder">The host's plan folder, repository-relative.</param>
    /// <param name="readText">Reads a changed path's text, or null when it cannot be read.</param>
    internal static IReadOnlyList<string> Find(
        IReadOnlyList<string>? changed,
        string? planFolder,
        Func<string, string?> readText)
    {
        ArgumentNullException.ThrowIfNull(readText);
        if (changed is null || string.IsNullOrWhiteSpace(planFolder))
            return [];

        string folder = planFolder.Replace('\\', '/').TrimEnd('/');
        List<string> plans = [];
        foreach (string path in changed.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!path.EndsWith(Markdown, StringComparison.OrdinalIgnoreCase))
                continue;

            int separator = path.LastIndexOf('/');
            string directory = separator < 0 ? string.Empty : path[..separator];
            if (!string.Equals(directory, folder, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(path), IndexFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (readText(path) is { } text && DeclaresPlan(text))
                plans.Add(path);
        }

        return [.. plans.Order(StringComparer.Ordinal)];
    }

    /// <summary>The seat context carrying the plan the packet names: one path, the several candidates, or neither.</summary>
    /// <param name="context">The cycle's shared seat context.</param>
    /// <param name="plans">The plans the change touches, as <see cref="Find"/> returned them.</param>
    internal static CouncilSeatContext WithPlan(CouncilSeatContext context, IReadOnlyList<string> plans)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plans);

        return plans.Count switch
        {
            1 => context with { PlanPath = plans[0] },
            > 1 => context with { PlanPath = null, PlanCandidates = plans },
            _ => context with { PlanPath = null, PlanCandidates = null },
        };
    }

    private static bool DeclaresPlan(string text)
    {
        FrontmatterReadResult frontmatter = MarkdownFrontmatterReader.Read(text);
        if (!frontmatter.HasFrontmatter)
            return false;

        try
        {
            Dictionary<string, object?>? map =
                MarkdownFrontmatterReader.Deserializer.Deserialize<Dictionary<string, object?>>(frontmatter.Yaml);
            return map is not null
                   && map.TryGetValue("doc-type", out object? value)
                   && value is string docType
                   && IsPlan(docType);
        }
        catch (YamlException)
        {
            // An archived plan may carry frontmatter that is not valid YAML; the one key this lookup reads is still
            // legible on its own line.
            foreach (string line in frontmatter.Yaml.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("doc-type:", StringComparison.Ordinal))
                    return IsPlan(trimmed["doc-type:".Length..]);
            }

            return false;
        }
    }

    private static bool IsPlan(string value) =>
        string.Equals(value.Trim().Trim('"', '\'').Trim(), PlanDocType, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
