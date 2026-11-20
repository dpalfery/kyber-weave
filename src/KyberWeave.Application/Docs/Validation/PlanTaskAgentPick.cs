using KyberWeave.Contracts.Host;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Asks the decision model, once per task file, which agent should do each task: the task's title, capabilities and
/// files against the name and description of every agent that takes implementation or documentation work (ADR 0082).
/// The same request also asks whether the architect's difficulty rating fits the task by the shipped rubric.
/// </summary>
/// <remarks>
/// Each task is its own question, so the model reads only that task's state (one batched request read several tasks'
/// state as one and answered every question at about 0.3). The requests run in parallel, so the cost is about one
/// round trip however many tasks the plan holds, and an unreachable model costs at most one timeout. Only an answer at or above the floor is kept; below it, or on any provider fault,
/// the task gets no pick and the conductor chooses from the capabilities, as it does when no model is configured.
/// The architect rates the task; the model only checks that rating and never changes it.
/// </remarks>
public sealed class PlanTaskAgentPick : IPlanTaskAgentPicker, IDisposable
{
    /// <summary>The confidence a pick needs to be written to the task file.</summary>
    public const double ConfidenceFloor = 0.8;

    /// <summary>The first tier of the step-up ladder.</summary>
    public const string EasyTier = "dev-easy";

    /// <summary>
    /// The tier that stands for the developer kind under the tiered roster. A pick returns it as the kind of
    /// worker; the task-file writer records the tier the rating gives, and the dispatch steps it up by failed attempts.
    /// </summary>
    public const string DeveloperKind = "dev-medium";

    /// <summary>The last tier of the step-up ladder, which a failed attempt never climbs past.</summary>
    public const string HardTier = "dev-hard";

    /// <summary>The agent a removed planning agent's schema and data-model work goes to.</summary>
    public const string ArchitectAgent = "architect";

    /// <summary>The agent that replaces the removed read-only platform agent.</summary>
    public const string PlatformResearcherAgent = "platform-researcher";

    /// <summary>The agent that replaces the removed delivery agent.</summary>
    public const string DevopsAgent = "devops";

    /// <summary>The three developer agents in step-up order (Requirement 3.2).</summary>
    public static IReadOnlyList<string> DeveloperTiers { get; } = [EasyTier, DeveloperKind, HardTier];

    /// <summary>
    /// The names the removal phase deletes, each mapped to the agent that does its work afterwards. A technology
    /// developer maps to <see cref="DeveloperKind"/>: the task's difficulty and failed attempts then choose the
    /// tier, as they do for every developer dispatch under the tiered roster.
    /// </summary>
    /// <remarks>
    /// The lookup ignores case because the name it answers comes from a plan's hand-written <c>Role:</c> line, which
    /// no check normalises: an author who writes <c>CSharp-Dev</c> must still reach the developer tier rather than
    /// have the conductor dispatch a name no installed agent answers to. The values stay the canonical lowercase
    /// agent names.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> RemovedAgents { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["csharp-dev"] = DeveloperKind,
            ["dal-dev"] = DeveloperKind,
            ["maui-dev"] = DeveloperKind,
            ["pulumi-dev"] = DeveloperKind,
            ["typescript-dev"] = DeveloperKind,
            ["react-dev"] = DeveloperKind,
            ["rust-dev"] = DeveloperKind,
            ["tauri-dev"] = DeveloperKind,
            ["go-dev"] = DeveloperKind,
            ["python-dev"] = DeveloperKind,
            ["test-dev"] = DeveloperKind,
            ["sql-database-architect"] = ArchitectAgent,
            ["azure-reader"] = PlatformResearcherAgent,
            ["github-devops"] = DevopsAgent,
        };

    /// <summary>The question every pick asks.</summary>
    public const string Question = "Which agent should do this task, judged by the abilities it needs and the files it changes?";

    /// <summary>The agreement question, asked in the same call as the pick, judged by the shipped difficulty rubric.</summary>
    public const string AgreementQuestion = "Does the architect's difficulty rating fit this task, judged by the rubric?";

    /// <summary>The manifest name of the one difficulty rubric the architect and the decision model both judge by.</summary>
    public const string RubricResourceName = "Squad.difficulty-rubric.md";

    private const string RulePrefix = "KW-DOC-TASKS-PICK-";
    private const string AgreementPrefix = "KW-DOC-TASKS-AGREE-";
    private const int MaxCapabilityCharacters = 1200;

    private readonly IArbiterProvider _provider;
    private readonly IReadOnlyList<ArbiterSquadAgent> _candidates;
    private readonly IEmbeddedResources _resources;
    private string? _rubric;
    private bool _rubricRead;

    /// <summary>Creates a picker over a provider, the agents it may choose from and the shipped content it reads.</summary>
    /// <param name="provider">The decision model.</param>
    /// <param name="candidates">The agents a task may go to, with their descriptions.</param>
    /// <param name="resources">The embedded shipped content, which holds the difficulty rubric.</param>
    public PlanTaskAgentPick(IArbiterProvider provider, IReadOnlyList<ArbiterSquadAgent> candidates, IEmbeddedResources resources)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(resources);
        _provider = provider;
        _candidates = candidates;
        _resources = resources;
    }

    /// <inheritdoc />
    public void Dispose() => (_provider as IDisposable)?.Dispose();

    /// <summary>
    /// The developer tier for a task: <c>min(hard, rated difficulty + failed attempts)</c>. A task with no rating
    /// is a medium task, as the task file writes it, so a dispatch that belongs to no task also starts at medium.
    /// </summary>
    /// <param name="difficulty">The rating in use: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    /// <param name="failedAttempts">How many attempts at this task already failed.</param>
    public static string DeveloperTier(string? difficulty, int failedAttempts)
    {
        int rated = difficulty?.Trim() switch
        {
            "easy" => 0,
            "hard" => DeveloperTiers.Count - 1,
            _ => 1,
        };

        // Long arithmetic: a retry count at the top of its range must clamp to the last tier, not wrap to a
        // negative index a few steps below zero.
        long climbed = Math.Min(
            DeveloperTiers.Count - 1,
            rated + (long)Math.Max(failedAttempts, 0));
        return DeveloperTiers[(int)climbed];
    }

    /// <summary>
    /// The sentence that refuses a dispatch to a technology developer under the tiered roster and names the tier the
    /// task belongs on. Both dispatch paths that refuse one — the task-prompt tool and the Arbiter's roster rule —
    /// read it from here, so the refusal a conductor sees is the same whichever path answered.
    /// </summary>
    /// <param name="named">The technology developer the dispatch named.</param>
    /// <param name="difficulty">The task's rating: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    /// <param name="failedAttempts">How many attempts at the task already failed, which steps a developer up.</param>
    public static string TieredDeveloperRefusal(string named, string? difficulty, int failedAttempts = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(named);
        string tier = DeveloperTier(difficulty, failedAttempts);
        return $"'{named}' is a worker-roster: technology agent, and this run is worker-roster: tiered. " +
            $"This task's developer is '{tier}' (tier = min(hard, rated difficulty + failed attempts)). " +
            $"Dispatch '{tier}' instead.";
    }

    /// <summary>
    /// The agent a removed name resolves to and the sentence that reports it, or null when the name is not one the
    /// removal deletes. A removed technology developer becomes the tier its task's difficulty gives; a removed
    /// platform or planning agent becomes the role that took its work over (Requirement 13.5).
    /// </summary>
    /// <param name="agent">The agent a plan, task file or dispatch names.</param>
    /// <param name="difficulty">The task's rating: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    /// <param name="failedAttempts">How many attempts at the task already failed, which steps a developer up.</param>
    public static RemovedAgentResolution? ResolveRemovedAgent(string? agent, string? difficulty, int failedAttempts = 0)
    {
        string? named = string.IsNullOrWhiteSpace(agent) ? null : agent.Trim();
        if (named is null || !RemovedAgents.TryGetValue(named, out string? replacement))
            return null;

        string resolved = string.Equals(replacement, DeveloperKind, StringComparison.Ordinal)
            ? DeveloperTier(difficulty, failedAttempts)
            : replacement;
        return new RemovedAgentResolution(named, resolved, $"'{named}' was removed; this task goes to '{resolved}'.");
    }

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
        string? rubric = Rubric();
        ArbiterFactSet facts = new ArbiterFactSet()
            .With($"{key}.title", TitleOf(task), ArbiterFactLabel.Derived)
            .With($"{key}.capabilities", Bounded(task.Capabilities ?? "(not stated)"), ArbiterFactLabel.Derived)
            .With($"{key}.files", string.Join(", ", task.Files ?? []), ArbiterFactLabel.Derived)
            .With($"{key}.task", task.Prompt ?? "(not stated)", ArbiterFactLabel.Asserted)
            .With($"{key}.architect-rating", TierLabel(task.Difficulty), ArbiterFactLabel.Derived)
            .With($"{key}.rubric", rubric ?? "(not available)", ArbiterFactLabel.Asserted);
        string id = RulePrefix + task.Id;
        string agreementId = AgreementPrefix + task.Id;
        List<ArbiterRule> rules = [RuleFor(task.Id!, key)];
        if (rubric is not null)
        {
            rules.Add(AgreementRuleFor(task.Id!, key));
        }

        ArbiterStep1BatchResult result;
        try
        {
            // One request per task carries both questions, so the check costs no second round trip.
            result = await _provider.AskStep1Async(rules, facts).ConfigureAwait(false);
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

        DifficultyAgreement? agreement = null;
        if (rubric is not null
            && result.AnswersByRule.TryGetValue(agreementId, out string? verdict)
            && verdict is DifficultyAgreement.Agreed or DifficultyAgreement.NotAgreed
            && result.ScoresByRule is not null
            && result.ScoresByRule.TryGetValue(agreementId, out ArbiterStep1Score? agreementScore)
            && agreementScore.Confidence is { } agreementConfidence
            && agreementConfidence >= ConfidenceFloor)
        {
            agreement = new DifficultyAgreement(verdict, Math.Round(agreementConfidence, 3));
        }

        return new PlanTaskAgentChoice(answer, Math.Round(sure, 3), agreement);
    }

    /// <summary>
    /// The shipped difficulty rubric, read through the embedded-content seam, without the leading note that says who
    /// else carries the same text. Null when the resource cannot be read, which asks no agreement question.
    /// </summary>
    private string? Rubric()
    {
        if (_rubricRead)
        {
            return _rubric;
        }

        _rubricRead = true;
        try
        {
            string text = _resources.ReadAllText(RubricResourceName);
            int end = text.IndexOf("-->", StringComparison.Ordinal);
            _rubric = (end >= 0 ? text[(end + 3)..] : text).Trim();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _rubric = null;
        }

        return _rubric;
    }

    /// <summary>The architect's rating with the tier the rubric reads it as: <c>easy (standard)</c> and so on.</summary>
    /// <param name="difficulty">The rating in use: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    public static string TierLabel(string? difficulty) => difficulty?.Trim() switch
    {
        "easy" => "easy (standard)",
        "medium" => "medium (intermediate)",
        "hard" => "hard (advanced)",
        _ => difficulty ?? "(none)",
    };

    /// <summary>The rubric's own name for a rating's tier: <c>standard</c>, <c>intermediate</c> or <c>advanced</c>.</summary>
    /// <param name="difficulty">The rating in use: <c>easy</c>, <c>medium</c> or <c>hard</c>, or null when none is recorded.</param>
    public static string TierNameOf(string? difficulty) => difficulty?.Trim() switch
    {
        "easy" => "standard",
        "medium" => "intermediate",
        "hard" => "advanced",
        _ => "unrated",
    };

    /// <summary>The agreement rule, judged by the same rubric the architect rated the task with, asked beside the pick.</summary>
    private static ArbiterRule AgreementRuleFor(string taskId, string key) => new(
        AgreementPrefix + taskId,
        "docs.tasks",
        AgreementQuestion,
        [DifficultyAgreement.Agreed, DifficultyAgreement.NotAgreed],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DifficultyAgreement.Agreed] = RuleEffects.Allow,
            [DifficultyAgreement.NotAgreed] = RuleEffects.Allow,
        },
        Ask: new RuleAsk(
            "choice",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = $"{key}.title",
                ["capabilities"] = $"{key}.capabilities",
                ["files"] = $"{key}.files",
                ["task"] = $"{key}.task",
                ["architect-rating"] = $"{key}.architect-rating",
                ["rubric"] = $"{key}.rubric",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DifficultyAgreement.Agreed] = "The rubric puts the task in the tier the architect chose.",
                [DifficultyAgreement.NotAgreed] = "The rubric puts the task in a different tier.",
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

/// <summary>A removed agent's replacement, and the sentence that reports the substitution.</summary>
/// <param name="Named">The removed agent the plan, task file or dispatch still names.</param>
/// <param name="Resolved">The agent that does the work now.</param>
/// <param name="Report">One sentence naming both, for the conductor's output.</param>
public sealed record RemovedAgentResolution(string Named, string Resolved, string Report);
