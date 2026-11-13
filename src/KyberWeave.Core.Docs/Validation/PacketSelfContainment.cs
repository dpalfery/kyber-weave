using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Asks the Arbiter's model, once per task packet, whether a developer who can read only that packet could do the
/// task. Whether prose says enough is a judgment about meaning, so a model answers it and a rule of thumb does not.
/// </summary>
/// <remarks>
/// The question follows the Arbiter's question design: one judgment (what the packet lacks, if anything), options
/// named for what they mean, criteria that describe concrete situations, and a confidence floor below which the
/// answer is ignored. A provider failure or a low-confidence answer reports nothing.
/// </remarks>
public static class PacketSelfContainment
{
    /// <summary>The answer meaning nothing is missing.</summary>
    public const string SelfContained = "self-contained";

    /// <summary>The confidence a not-self-contained answer needs to be reported.</summary>
    public const double ConfidenceFloor = 0.8;

    private const int MaxPacketCharacters = 12000;

    private static readonly IReadOnlyDictionary<string, string> MissingElement = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["missing-files"] = "which files to create or change",
        ["missing-behaviour"] = "the behaviour the finished work must have",
        ["missing-tests"] = "the tests to write or make pass",
        ["missing-command"] = "the exact command to run",
    };

    /// <summary>The question, as the provider is asked it.</summary>
    public static ArbiterRule Rule { get; } = new(
        PlanTaskFileValidator.PacketNotSelfContained,
        "docs.tasks",
        "Could a developer who can read only this packet, not the plan, do the task: are the files, the expected behaviour, the tests to write or pass, and the exact command all stated here?",
        [SelfContained, "missing-files", "missing-behaviour", "missing-tests", "missing-command"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SelfContained] = RuleEffects.Allow,
            ["missing-files"] = RuleEffects.Advise,
            ["missing-behaviour"] = RuleEffects.Advise,
            ["missing-tests"] = RuleEffects.Advise,
            ["missing-command"] = RuleEffects.Advise,
        },
        Ask: new RuleAsk(
            "choice",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["packet"] = "packet.text", ["files"] = "packet.files" },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SelfContained] =
                    "The packet itself names the files to change, says what the finished work must do, names each test to write or make pass by name or by what it checks (a task that owes no tests names none), and gives the exact command to run. Someone who never saw the plan could start work.",
                ["missing-files"] =
                    "The packet does not say which files to create or change, or points at the plan for them.",
                ["missing-behaviour"] =
                    "The packet names a topic but not what the finished work must do: inputs and outputs, error cases, and layout or styling where the task is about those are missing, or are only referred to by id or by section name without their content.",
                ["missing-tests"] =
                    "The packet asks for tests to be written or to pass but gives no test name and no description of what any test checks. Telling the developer to write the tests from the plan, from a test contract, or from a decision, when no such content appears in the packet, is missing-tests. A task that owes no tests is not missing-tests.",
                ["missing-command"] =
                    "The packet gives no exact command to run to check the work, or only a description of one.",
            },
            ConfidenceAtLeast: ConfidenceFloor));

    /// <summary>One warning per packet the model confidently says is not self-contained.</summary>
    /// <param name="provider">The model provider.</param>
    /// <param name="file">The task file whose packets are asked about.</param>
    /// <param name="artifactPath">The plan, for the findings.</param>
    /// <param name="answers">Receives each task's answer and confidence, so a caller can show them.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public static async Task<IReadOnlyList<Diagnostic>> CheckAsync(
        IArbiterProvider provider,
        PlanTaskFile file,
        string artifactPath,
        IDictionary<string, (string Answer, double? Confidence)>? answers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(file);

        List<Diagnostic> findings = [];
        foreach (PlanTaskFileEntry entry in file.Tasks ?? [])
        {
            string packet = Packet(entry);
            ArbiterFactSet facts = new ArbiterFactSet()
                .With("packet.text", packet.Length > MaxPacketCharacters ? packet[..MaxPacketCharacters] : packet, ArbiterFactLabel.Asserted)
                .With("packet.files", entry.Files ?? [], ArbiterFactLabel.Derived);
            ArbiterStep1BatchResult result = await provider
                .AskStep1Async([Rule], facts, null, cancellationToken)
                .ConfigureAwait(false);
            if (!result.AnswersByRule.TryGetValue(Rule.Id, out string? answer))
            {
                continue;
            }

            double? confidence = result.ScoresByRule is not null && result.ScoresByRule.TryGetValue(Rule.Id, out ArbiterStep1Score? score)
                ? score.Confidence
                : null;
            answers?.Add(entry.Id ?? "?", (answer, confidence));
            if (MissingElement.TryGetValue(answer, out string? missing))
            {
                findings.Add(new Diagnostic(
                    PlanTaskFileValidator.PacketNotSelfContained,
                    Severity.Warning,
                    $"Task '{entry.Id}': a developer who reads only this packet could not do the task; it does not state {missing}.",
                    entry.Id ?? "?",
                    artifactPath,
                    Hint: $"State {missing} in task '{entry.Id}' in the plan (copy it in, do not point at another section), " +
                          $"then run {TaskFileCommand.Write(artifactPath)} and check again."));
            }
        }

        return findings;
    }

    /// <summary>The packet as the model reads it: the prompt without the slots the conductor fills at dispatch.</summary>
    internal static string Packet(PlanTaskFileEntry entry)
    {
        string prompt = entry.Prompt ?? string.Empty;
        int prior = prompt.IndexOf("\nResults of the tasks this one follows", StringComparison.Ordinal);
        return (prior < 0 ? prompt : prompt[..prior]).Replace("{worktree}", "<the worktree>", StringComparison.Ordinal).Trim();
    }
}
