using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>One task's agent pick: the agent the model chose and how sure it was.</summary>
/// <param name="Agent">The chosen agent's name.</param>
/// <param name="Confidence">The model's confidence, at or above <see cref="PlanTaskAgentPick.ConfidenceFloor"/>.</param>
public sealed record PlanTaskAgentChoice(string Agent, double Confidence);

/// <summary>Chooses an agent for each task of a task file while the file is written.</summary>
public interface IPlanTaskAgentPicker
{
    /// <summary>
    /// The confident picks by task id. A task with no confident pick is left out, and a picker that cannot reach its
    /// model returns no picks at all rather than failing.
    /// </summary>
    /// <param name="tasks">The tasks that need an agent: the plan assigned them no role.</param>
    IReadOnlyDictionary<string, PlanTaskAgentChoice> Pick(IReadOnlyList<PlanTaskFileEntry> tasks);
}

/// <summary>
/// Asks the decision model, once per task file, which agent should do each task: the task's title, capabilities and
/// files against the name and description of every agent that takes implementation or documentation work (ADR 0082).
/// </summary>
/// <remarks>
/// Each task is its own question, so the model reads only that task's state (one batched request read several tasks'
/// state as one and answered every question at about 0.3). The requests run in parallel, so the cost is about one
/// round trip however many tasks the plan holds, and an unreachable model costs at most one timeout. Only an answer at or above the floor is kept; below it, or on any provider fault,
/// the task gets no pick and the conductor chooses from the capabilities, as it does when no model is configured.
/// </remarks>
public sealed class PlanTaskAgentPick : IPlanTaskAgentPicker, IDisposable
{
    /// <summary>
    /// Builds the picker for a repository's configuration, or null when no decision model is configured or its key is
    /// missing. Settable so a test run never reaches a paid model unless a test installs one.
    /// </summary>
    public static Func<KyberWeaveConfig, PlanTaskAgentPick?> ForConfig { get; set; } = DefaultForConfig;

    /// <summary>The confidence a pick needs to be written to the task file.</summary>
    public const double ConfidenceFloor = 0.8;

    /// <summary>The question every pick asks.</summary>
    public const string Question = "Which agent should do this task, judged by the abilities it needs and the files it changes?";

    private const string RulePrefix = "KW-DOC-TASKS-PICK-";
    private const int MaxCapabilityCharacters = 1200;

    private readonly IArbiterProvider _provider;
    private readonly IReadOnlyList<ArbiterSquadAgent> _candidates;

    /// <summary>Creates a picker over a provider and the agents it may choose from.</summary>
    /// <param name="provider">The decision model.</param>
    /// <param name="candidates">The agents a task may go to, with their descriptions.</param>
    public PlanTaskAgentPick(IArbiterProvider provider, IReadOnlyList<ArbiterSquadAgent> candidates)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(candidates);
        _provider = provider;
        _candidates = candidates;
    }

    /// <inheritdoc />
    public void Dispose() => (_provider as IDisposable)?.Dispose();

    private static PlanTaskAgentPick? DefaultForConfig(KyberWeaveConfig config)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            return null;
        IArbiterProvider? provider = ConfiguredProvider.TryCreate(config, home);
        return provider is null ? null : new PlanTaskAgentPick(provider, ImplementationAgents());
    }

    /// <summary>
    /// The agents a task may go to: every agent of the installed Squad whose capability profile takes implementation or
    /// documentation work, read from the agent files the build embeds, so a new agent file is a new candidate.
    /// </summary>
    public static IReadOnlyList<ArbiterSquadAgent> ImplementationAgents() =>
        [.. ArbiterSquadCatalog.LoadEmbedded().Agents.Values
            .Where(agent => string.Equals(agent.TargetClass, "implementation", StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(agent.Description))
            .OrderBy(agent => agent.Name, StringComparer.Ordinal)];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, PlanTaskAgentChoice> Pick(IReadOnlyList<PlanTaskFileEntry> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        Dictionary<string, PlanTaskAgentChoice> picks = new(StringComparer.Ordinal);
        List<PlanTaskFileEntry> asked = [.. tasks.Where(task => !string.IsNullOrWhiteSpace(task.Id))
            .Take(StateBudget.MaxQuestions)];
        if (asked.Count == 0 || _candidates.Count < 2)
            return picks;

        HashSet<string> names = [.. _candidates.Select(agent => agent.Name)];
        PlanTaskAgentChoice?[] choices;
        try
        {
            choices = Task.WhenAll(asked.Select(AskAsync)).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return picks;
        }

        for (int index = 0; index < asked.Count; index++)
        {
            if (choices[index] is { } choice && names.Contains(choice.Agent))
                picks[asked[index].Id!] = choice;
        }

        return picks;
    }

    /// <summary>One task's question, asked on its own so the model reads only that task's state.</summary>
    private async Task<PlanTaskAgentChoice?> AskAsync(PlanTaskFileEntry task)
    {
        const string key = "pick";
        ArbiterFactSet facts = new ArbiterFactSet()
            .With($"{key}.title", TitleOf(task), ArbiterFactLabel.Derived)
            .With($"{key}.capabilities", Bounded(task.Capabilities ?? "(not stated)"), ArbiterFactLabel.Derived)
            .With($"{key}.files", string.Join(", ", task.Files ?? []), ArbiterFactLabel.Derived);
        string id = RulePrefix + task.Id;
        ArbiterStep1BatchResult result;
        try
        {
            result = await _provider.AskStep1Async([RuleFor(task.Id!, key)], facts).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }

        if (!result.AnswersByRule.TryGetValue(id, out string? answer))
            return null;
        double? confidence = result.ScoresByRule is not null && result.ScoresByRule.TryGetValue(id, out ArbiterStep1Score? score)
            ? score.Confidence
            : null;
        return confidence is { } sure && sure >= ConfidenceFloor ? new PlanTaskAgentChoice(answer, Math.Round(sure, 3)) : null;
    }

    private ArbiterRule RuleFor(string taskId, string key) => new(
        RulePrefix + taskId,
        "docs.tasks",
        Question,
        [.. _candidates.Select(agent => agent.Name)],
        _candidates.ToDictionary(agent => agent.Name, _ => RuleEffects.Allow, StringComparer.Ordinal),
        Ask: new RuleAsk(
            "choice",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = $"{key}.title",
                ["capabilities"] = $"{key}.capabilities",
                ["files"] = $"{key}.files",
            },
            _candidates.ToDictionary(agent => agent.Name, agent => agent.Description, StringComparer.Ordinal),
            ConfidenceAtLeast: ConfidenceFloor));

    private static string Bounded(string text) =>
        text.Length > MaxCapabilityCharacters ? text[..MaxCapabilityCharacters] : text;

    private static string TitleOf(PlanTaskFileEntry task)
    {
        string first = (task.Prompt ?? string.Empty).Split('\n', 2)[0].Trim();
        string prefix = $"Task {task.Id}";
        return first.StartsWith(prefix, StringComparison.Ordinal) ? first[prefix.Length..].TrimStart(':', ' ') : first;
    }
}
