using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Validation;

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
    /// <summary>The confidence a pick needs to be written to the task file.</summary>
    public const double ConfidenceFloor = 0.8;

    /// <summary>The first tier of the step-up ladder.</summary>
    public const string EasyTier = "dev-easy";

    /// <summary>
    /// The tier that stands for the developer kind under the tiered roster: the pick records the kind, and
    /// the task's difficulty and failed attempts then choose the tier.
    /// </summary>
    public const string DeveloperKind = "dev-medium";

    /// <summary>The last tier of the step-up ladder, which a failed attempt never climbs past.</summary>
    public const string HardTier = "dev-hard";

    /// <summary>The three developer agents in step-up order (Requirement 3.2).</summary>
    public static IReadOnlyList<string> DeveloperTiers { get; } = [EasyTier, DeveloperKind, HardTier];

    /// <summary>The question every pick asks.</summary>
    public const string Question = "Which agent should do this task, judged by the abilities it needs and the files it changes?";

    /// <summary>The difficulty question, asked in the same call as the pick (the shipped <c>KW-ARB-DIFFICULTY-001</c> question).</summary>
    public const string DifficultyQuestion = "How hard is this task: easy, standard or hard?";

    /// <summary>The answer that maps to <c>medium</c>; the shipped rule spells the middle value <c>standard</c>.</summary>
    public const string StandardAnswer = "standard";

    private const string RulePrefix = "KW-DOC-TASKS-PICK-";
    private const string DifficultyPrefix = "KW-DOC-TASKS-RATE-";
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

    /// <summary>
    /// The agents a task may go to under both rosters: every agent of the installed Squad whose capability profile
    /// takes implementation or documentation work, read from the agent files the build embeds, so a new agent file
    /// is a new candidate.
    /// </summary>
    public static IReadOnlyList<ArbiterSquadAgent> ImplementationAgents() => ImplementationAgents(null);

    /// <summary>
    /// The agents a task may go to under <paramref name="roster"/>. An agent that declares no roster serves both,
    /// so the agents that stay under either roster are always candidates. Under the tiered roster the three
    /// developer agents are one candidate, <see cref="DeveloperKind"/>: the pick chooses the kind of worker and
    /// the difficulty then chooses the tier (Requirement 3.1). A null roster offers every agent, as the
    /// decision-model tests ask.
    /// </summary>
    /// <param name="roster">The active roster, <c>technology</c> or <c>tiered</c>; null for both.</param>
    public static IReadOnlyList<ArbiterSquadAgent> ImplementationAgents(string? roster)
    {
        IEnumerable<ArbiterSquadAgent> agents = ArbiterSquadCatalog.LoadEmbedded().Agents.Values
            .Where(agent => string.Equals(agent.TargetClass, "implementation", StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(agent.Description));

        if (roster is not null)
        {
            agents = agents.Where(agent => agent.WorkerRoster is null
                                           || string.Equals(agent.WorkerRoster, roster, StringComparison.Ordinal));
        }

        if (string.Equals(roster, WorkerRosters.Tiered, StringComparison.Ordinal))
        {
            agents = agents.Where(agent => agent.Name is not (EasyTier or HardTier));
        }

        return [.. agents.OrderBy(agent => agent.Name, StringComparer.Ordinal)];
    }

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
        string difficultyId = DifficultyPrefix + task.Id;
        ArbiterStep1BatchResult result;
        try
        {
            // One request per task carries both questions, so the rating costs no second round trip.
            result = await _provider.AskStep1Async(
                [RuleFor(task.Id!, key), DifficultyRuleFor(task.Id!, key)], facts).ConfigureAwait(false);
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
        if (confidence is not { } sure || sure < ConfidenceFloor)
        {
            return null;
        }

        string? difficulty = null;
        double? difficultyConfidence = null;
        if (result.AnswersByRule.TryGetValue(difficultyId, out string? rated)
            && result.ScoresByRule is not null
            && result.ScoresByRule.TryGetValue(difficultyId, out ArbiterStep1Score? difficultyScore)
            && difficultyScore.Confidence >= ConfidenceFloor
            && DifficultyOf(rated) is { } ratedDifficulty)
        {
            difficulty = ratedDifficulty;
            difficultyConfidence = Math.Round(difficultyScore.Confidence!.Value, 3);
        }

        return new PlanTaskAgentChoice(answer, Math.Round(sure, 3), difficulty, difficultyConfidence);
    }

    /// <summary>
    /// The difficulty to record for an answer, or null when the answer is not one the difficulty
    /// question offered. A value that is not a legal difficulty is never recorded: the agent's name
    /// or a model the question did not list would otherwise be written into the task file as a
    /// difficulty, and the task-file check would refuse the plan.
    /// </summary>
    private static string? DifficultyOf(string? answer) => answer switch
    {
        "easy" => "easy",
        StandardAnswer => "medium",
        "hard" => "hard",
        _ => null,
    };

    /// <summary>The difficulty rule, with the shipped rule's question, answers and criteria, asked beside the pick.</summary>
    private static ArbiterRule DifficultyRuleFor(string taskId, string key) => new(
        DifficultyPrefix + taskId,
        "docs.tasks",
        DifficultyQuestion,
        ["easy", StandardAnswer, "hard"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["easy"] = RuleEffects.Allow,
            [StandardAnswer] = RuleEffects.Allow,
            ["hard"] = RuleEffects.Allow,
        },
        Ask: new RuleAsk(
            "choice",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = $"{key}.title",
                ["capabilities"] = $"{key}.capabilities",
                ["files"] = $"{key}.files",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["easy"] = "A small, local change with one obvious path.",
                [StandardAnswer] = "A change with several files or a non-obvious path, but no cross-cutting risk.",
                ["hard"] = "A change that spans layers, alters contracts, or needs careful design.",
            },
            ConfidenceAtLeast: ConfidenceFloor));

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
