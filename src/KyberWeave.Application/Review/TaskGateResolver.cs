using KyberWeave.Core.Configuration;
using KyberWeave.Core.Parsing;
using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Application.Review;

/// <summary>Where a task's gates were read from.</summary>
public enum TaskGateSource
{
    /// <summary>Nothing resolved: no entry covers the agent or its technology.</summary>
    None,

    /// <summary>The agent's own <c>review.task-gates</c> entry.</summary>
    Agent,

    /// <summary>The <c>review.technology-gates</c> entry for the task's technology or its parent.</summary>
    Technology,

    /// <summary>The entry of the technology agent the shipped map names for the task's technology or its parent.</summary>
    MappedAgent
}

/// <summary>The gates one task's agent runs, and where they came from.</summary>
/// <param name="Gates">The gates to run, in declaration order; empty when none resolved.</param>
/// <param name="Source">The kind of entry the gates were read from.</param>
/// <param name="EntryKey">The agent slug or technology name the gates were read under, or null.</param>
/// <param name="Technology">The technology that resolved them — the task's own, or its parent — or null.</param>
/// <param name="Refusal">
/// The reason a task that names a technology resolved nothing, or null when a resolution or an
/// agent-only lookup without a technology left nothing to report.
/// </param>
public sealed record TaskGateResolution(
    IReadOnlyList<ReviewTaskGate> Gates,
    TaskGateSource Source,
    string? EntryKey,
    string? Technology,
    string? Refusal)
{
    /// <summary>Whether a technology was named and nothing resolved, so the caller must refuse the gate run.</summary>
    public bool IsRefused => Refusal is not null;
}

/// <summary>
/// Resolves the gates one task runs from the one place that knows: the agent's own entry, then the task
/// technology's entry, then the entry of the technology agent the shipped map names, then the parent
/// technology's entry.
/// </summary>
/// <remarks>
/// A task names one technology, so the lists are never merged and no gate is added: the first entry that
/// exists is the task's gate list. The council's <c>review.gates</c> are a different list and are not read here.
/// An agent that resolves nothing when no technology is named is reported by its caller as a warning, which is
/// what it did before this resolver existed; a task that names a technology and resolves nothing is refused,
/// because a developer task with no gate has no evidence behind it.
/// </remarks>
public static class TaskGateResolver
{
    private static readonly IReadOnlyList<ReviewTaskGate> None = [];

    /// <summary>Resolves the gates for one agent and, when the task names one, one technology.</summary>
    /// <param name="review">The host's <c>review:</c> configuration.</param>
    /// <param name="agent">The agent working the task, or null when unknown.</param>
    /// <param name="technology">The one technology the task names, or null for a task that names none.</param>
    /// <param name="parentTechnology">The technology <paramref name="technology"/> builds on, from <see cref="ParentTechnology"/>.</param>
    public static TaskGateResolution Resolve(
        ReviewConfig review,
        string? agent,
        string? technology = null,
        string? parentTechnology = null)
    {
        ArgumentNullException.ThrowIfNull(review);

        string? agentName = Clean(agent);
        string? technologyName = Clean(technology);

        if (agentName is not null
            && review.TaskGates.TryGetValue(agentName, out IReadOnlyList<ReviewTaskGate>? own)
            && own.Count > 0)
        {
            return new TaskGateResolution(own, TaskGateSource.Agent, agentName, technologyName, null);
        }

        foreach (string candidate in Candidates(technologyName, Clean(parentTechnology)))
        {
            if (review.TechnologyGates.TryGetValue(candidate, out IReadOnlyList<ReviewTaskGate>? byTechnology)
                && byTechnology.Count > 0)
            {
                return new TaskGateResolution(byTechnology, TaskGateSource.Technology, candidate, candidate, null);
            }

            string? mapped = TechnologyAgentMap.AgentFor(candidate);
            if (mapped is not null
                && review.TaskGates.TryGetValue(mapped, out IReadOnlyList<ReviewTaskGate>? byAgent)
                && byAgent.Count > 0)
            {
                return new TaskGateResolution(byAgent, TaskGateSource.MappedAgent, mapped, candidate, null);
            }
        }

        return technologyName is null
            ? new TaskGateResolution(None, TaskGateSource.None, null, null, null)
            : new TaskGateResolution(None, TaskGateSource.None, null, technologyName, RefusalFor(agentName, technologyName));
    }

    /// <summary>
    /// The technology a template names in its standard's <c>builds-on</c> key, or null when it names none, has
    /// no standard of its own, cannot read its standard, or the name is not something a folder can be called.
    /// </summary>
    /// <param name="repoRoot">The repository root the standards folders sit under.</param>
    /// <param name="config">The effective configuration, whose ontology names the documentation root.</param>
    /// <param name="technology">The task's technology.</param>
    public static string? ParentTechnology(string repoRoot, KyberWeaveConfig config, string? technology)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        string? name = Clean(technology);
        if (name is null || !ConfigSlug.IsValid(name))
            return null;

        string docsRoot = Path.Combine(repoRoot, config.Ontology.DocsRoot);
        string[] candidates =
        [
            DocsLayout.StandardFile(docsRoot, name),
            $"{docsRoot}/{DocsLayout.Standards}/{name}/README.md"
        ];

        foreach (string path in candidates)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                FrontmatterReadResult read = MarkdownFrontmatterReader.Read(File.ReadAllText(path));
                if (!read.HasFrontmatter)
                    continue;

                StandardBuildsOn parent = MarkdownFrontmatterReader.Deserializer.Deserialize<StandardBuildsOn>(read.Yaml);
                if (Clean(parent.BuildsOn) is { } buildsOn && ConfigSlug.IsValid(buildsOn))
                    return buildsOn;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An unreadable or malformed standard names no parent. Gate resolution falls back to the mapped
                // agent's entry, which is what it does for a technology with no standard of its own.
                continue;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string? technology, string? parent)
    {
        if (technology is not null)
            yield return technology;

        if (parent is not null && !string.Equals(parent, technology, StringComparison.Ordinal))
            yield return parent;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RefusalFor(string? agent, string technology)
    {
        string who = agent is null ? string.Empty : $" for agent '{agent}'";
        return $"No task gates resolve{who} in technology '{technology}'. Declare them under " +
               $"review.technology-gates.{technology}, or give the technology's agent an entry under review.task-gates.";
    }

    /// <summary>The one frontmatter key this resolver reads from a standard.</summary>
    private sealed class StandardBuildsOn
    {
        public string? BuildsOn { get; init; }
    }
}
