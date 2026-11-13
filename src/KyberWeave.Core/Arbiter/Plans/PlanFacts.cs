using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter.Plans;

/// <summary>
/// The plan facts every host reads, from the plan file the dispatch names: the hook and the CLI dry run
/// share this one reader.
/// </summary>
/// <remarks>
/// <para>
/// Plan and task identity come from <c>PLAN_FILE</c> and <c>TASK</c>; nothing is inferred from the plan index.
/// A validated header wins. When the header was rejected or written loosely (another case, a space in the
/// path, an absolute path inside the repository), the raw value is tried too: a file that resolves inside the
/// repository and exists is a machine fact, so the dispatch reads its plan instead of being judged on a typo.
/// </para>
/// <para>
/// The header is caller-controlled, so the path has to resolve inside the repository; one that does not is
/// <c>plan.exists: false</c> like any other unreadable plan. A missing or unreadable plan is that fact, not a
/// failure.
/// </para>
/// </remarks>
public static class PlanFacts
{
    /// <summary>The fact listing every task the plan holds, one <c>id: title</c> line each.</summary>
    public const string TaskListFact = "plan.task-list";

    /// <summary>The longest task title the task list carries.</summary>
    private const int MaxTitleChars = 160;

    /// <summary>Merges the plan facts for <paramref name="classification"/> into <paramref name="facts"/>.</summary>
    public static ArbiterFactSet Enrich(ArbiterFactSet facts, TriggerClassification classification, string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(repositoryRoot);

        string? planFile = HeaderValue(classification, "PLAN_FILE");
        if (planFile is null)
        {
            return facts;
        }

        string? resolved = PlanPathResolver.Resolve(planFile, repositoryRoot);
        if (resolved is null || !File.Exists(resolved))
        {
            return facts.With("plan.exists", false, ArbiterFactLabel.Derived);
        }

        PlanDocument document;
        string content;
        try
        {
            content = File.ReadAllText(resolved);
            document = PlanDocumentParser.Parse(content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return facts.With("plan.exists", false, ArbiterFactLabel.Derived);
        }

        // The digest of the text just parsed. REPEAT is keyed on it so that amending a plan restarts the count;
        // the evaluator reads this fact rather than hashing the file a second time.
        facts = facts.With("plan.digest", PlanDocumentParser.Digest(content), ArbiterFactLabel.Derived);
        facts = facts.With("plan.exists", true, ArbiterFactLabel.Derived);
        if (document.Status is not null)
        {
            facts = facts.With("plan.status", document.Status, ArbiterFactLabel.Derived);
        }

        facts = facts.With("plan.development-mode", document.DevelopmentMode, ArbiterFactLabel.Derived);
        facts = facts.With("plan.tasks.count", document.Tasks.Count, ArbiterFactLabel.Derived);
        facts = facts.With("plan.out-of-scope", document.OutOfScope.ToList(), ArbiterFactLabel.Derived);
        facts = facts.With(TaskListFact, TaskList(document), ArbiterFactLabel.Derived);

        PlanTask? task = FindTask(document, HeaderValue(classification, "TASK"));
        if (task is not null)
        {
            facts = facts.With("plan.task", task.Id, ArbiterFactLabel.Derived);
            facts = facts.With("plan.task.text", task.Text, ArbiterFactLabel.Derived);
            facts = facts.With("plan.task.files", task.Files.ToList(), ArbiterFactLabel.Derived);
            facts = facts.With("plan.task.depends-on", task.DependsOn.ToList(), ArbiterFactLabel.Derived);
            facts = facts.With("plan.task.skills", task.Skills.ToList(), ArbiterFactLabel.Derived);
            facts = facts.With(
                "plan.test-contract.row",
                document.RedEvidenceRequirement(task.Id),
                ArbiterFactLabel.Derived);
        }

        return facts;
    }

    /// <summary>The validated header value, else the raw one the prompt wrote, else null.</summary>
    private static string? HeaderValue(TriggerClassification classification, string name)
    {
        if (classification.Headers.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return classification.RawHeaders.TryGetValue(name, out string? raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : null;
    }

    /// <summary>
    /// The task the value names: an exact id, or else the task whose id is the value's first word once
    /// surrounding punctuation is dropped (<c>T3 — add the parser</c> names <c>T3</c>).
    /// </summary>
    private static PlanTask? FindTask(PlanDocument document, string? value)
    {
        if (value is null)
        {
            return null;
        }

        PlanTask? exact = document.Tasks.FirstOrDefault(
            candidate => string.Equals(candidate.Id, value, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        string first = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        string word = first.Trim(':', ',', '.', ';', '(', ')', '[', ']', '`', '*');
        return word.Length == 0
            ? null
            : document.Tasks.FirstOrDefault(
                candidate => string.Equals(candidate.Id, word, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> TaskList(PlanDocument document) =>
        [.. document.Tasks.Select(Line)];

    private static string Line(PlanTask task) =>
        task.Title.Length > MaxTitleChars
            ? $"{task.Id}: {task.Title[..MaxTitleChars]}"
            : $"{task.Id}: {task.Title}";
}
