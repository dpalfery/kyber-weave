using System.Text.RegularExpressions;
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
public static partial class PlanFacts
{
    /// <summary>
    /// How the dispatch's plan and task were identified: <c>headers</c> when the dispatch wrote <c>PLAN_FILE</c> or
    /// <c>TASK</c>, <c>machine</c> when neither was written and they were resolved from the run's worktree instead.
    /// </summary>
    public const string IdentityFact = "delegation.identity";

    /// <summary>The value of <see cref="IdentityFact"/> for a dispatch that names its plan or task in headers.</summary>
    public const string HeaderIdentity = "headers";

    /// <summary>The value of <see cref="IdentityFact"/> for a dispatch whose identity came from machine facts.</summary>
    public const string MachineIdentity = "machine";

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
        string? taskValue = HeaderValue(classification, "TASK");
        // A dispatch that wrote any routing header is judged on its headers; one that wrote none is identified from
        // machine facts instead (ADR 0082).
        bool marked = classification.Headers.ContainsKey("KYBER-ARBITER") || classification.RawHeaders.ContainsKey("KYBER-ARBITER");
        bool machine = planFile is null && taskValue is null && !marked && IsDelegation(classification.Trigger);
        if (IsDelegation(classification.Trigger))
        {
            facts = facts.With(IdentityFact, machine ? MachineIdentity : HeaderIdentity, ArbiterFactLabel.Derived);
        }

        if (machine)
        {
            // No header names the plan: the run's own worktree does. Exactly one Ready or In-progress plan there is
            // the run's plan; none or several leave the plan unknown, and the rules allow what they cannot name.
            planFile = ActivePlan(facts, repositoryRoot);
        }

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

        PlanTask? task = machine
            ? TaskNamedIn(document, facts)
            : FindTask(document, taskValue);
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

    private static bool IsDelegation(string? trigger) =>
        string.Equals(trigger, "delegate", StringComparison.Ordinal)
        || string.Equals(trigger, "delegate.returned", StringComparison.Ordinal);

    /// <summary>
    /// The one Ready or In-progress plan directly in a planning folder of <paramref name="repositoryRoot"/>, or a
    /// specification folder's <c>tasks.md</c>; null when there is none or more than one.
    /// </summary>
    internal static string? ActivePlan(ArbiterFactSet facts, string repositoryRoot)
    {
        IEnumerable<string> globs = facts.TryGet("config.planning-dirs", out ArbiterFact? dirs) && dirs.Value is IEnumerable<string> list
            ? list
            : [];
        List<string> active = [];
        foreach (string glob in globs)
        {
            string folder = glob.EndsWith("/**", StringComparison.Ordinal) ? glob[..^3] : glob;
            if (folder.Length == 0)
                continue;
            string directory = Path.Combine(repositoryRoot, folder.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                IEnumerable<string> candidates = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                    .Where(file => !string.Equals(Path.GetFileName(file), "README.md", StringComparison.OrdinalIgnoreCase))
                    .Concat(Directory.EnumerateDirectories(directory)
                        .Select(sub => Path.Combine(sub, "tasks.md"))
                        .Where(File.Exists));
                foreach (string file in candidates)
                {
                    if (ActiveStatusRegex().IsMatch(File.ReadAllText(file)))
                        active.Add(Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/'));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        List<string> distinct = [.. active.Distinct(StringComparer.Ordinal)];
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// The plan task a headerless dispatch names (see <see cref="TaskNamedIn(PlanDocument, string)"/>).
    /// </summary>
    private static PlanTask? TaskNamedIn(PlanDocument document, ArbiterFactSet facts) =>
        facts.TryGet("delegation.prompt", out ArbiterFact? prompt) && prompt.Value is string text
            ? TaskNamedIn(document, text)
            : null;

    /// <summary>
    /// The plan task a headerless dispatch names: one task label at the start of a line (<c>TASK_ID: T3</c>,
    /// <c>TASK: T3</c>, <c>Task T3</c>) wins, and labels naming several tasks leave it unknown; otherwise the one task id of the plan that the text names, with
    /// <c>task</c> or <c>packet</c> before it. Several ids, or none, leave the task unknown.
    /// </summary>
    internal static PlanTask? TaskNamedIn(PlanDocument document, string prompt)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(prompt);

        List<PlanTask> labelled = [];
        foreach (Match label in TaskLabelRegex().Matches(prompt))
        {
            PlanTask? found = FindTask(document, label.Groups["id"].Value);
            if (found is not null && !labelled.Contains(found))
                labelled.Add(found);
        }

        // One labelled task names the dispatch's task; several (a closeout listing every task) name none.
        if (labelled.Count > 0)
            return labelled.Count == 1 ? labelled[0] : null;

        List<PlanTask> named = [];
        foreach (Match mention in TaskMentionRegex().Matches(prompt))
        {
            PlanTask? found = FindTask(document, mention.Groups["id"].Value);
            if (found is not null && !named.Contains(found))
                named.Add(found);
        }

        return named.Count == 1 ? named[0] : null;
    }

    [GeneratedRegex(@"\*\*Status:(?:\*\*)?\s*(?:Ready|In[ -]progress)(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveStatusRegex();

    [GeneratedRegex(@"^[ \t]*(?:TASK_ID|TASK|Task)[ \t]*[:#]?[ \t]*(?<id>[A-Za-z]{0,4}-?\d+[A-Za-z0-9.]*)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TaskLabelRegex();

    [GeneratedRegex(@"\b(?:task|packet)(?:[ \t]*[:#\u2014\u2013-])?[ \t]+(?<id>[A-Za-z]{0,4}-?\d+[A-Za-z0-9.]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskMentionRegex();

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
