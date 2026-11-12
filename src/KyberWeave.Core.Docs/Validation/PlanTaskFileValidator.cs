using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks each plan's task file against the plan it was written from, and rules on the prompts
/// it carries.
/// </summary>
/// <remarks>
/// <para>
/// The task file exists so the conductor can dispatch without reading the plan. The plan stays
/// the authority, so the file has a shelf life of one plan and is valid only while it still
/// describes it: this validator is what stops a conductor pasting a prompt the plan has moved
/// past, or a prompt that sends the worker back into the plan the whole arrangement exists to
/// keep out of the conductor's context.
/// </para>
/// <para>
/// It runs in <c>docs validate</c> and in the docs MCP validate tool, not behind a flag, because
/// the orchestrator holds the docs tools and no shell: a rule a human has to type a flag to
/// reach is a rule the run never applies.
/// </para>
/// <para>
/// The one warning, <see cref="MissingTaskFile" />, is reported rather than enforced: every plan
/// sits in that state until its architect writes the file, so failing on it would fail every
/// merge that carries an unconverted plan. The rest are errors, because each means a file that
/// exists and says something the plan does not.
/// </para>
/// </remarks>
public sealed partial class PlanTaskFileValidator
{
    /// <summary>The file is not JSON this build reads, or declares a schema version it does not read.</summary>
    public const string Malformed = "KW-DOC-TASKS-001";

    /// <summary>The file's plan, tasks, files or dependencies disagree with the plan's.</summary>
    public const string DiffersFromPlan = "KW-DOC-TASKS-002";

    /// <summary>The file's execution digest is stale: the plan moved after the file was written.</summary>
    public const string StaleDigest = "KW-DOC-TASKS-003";

    /// <summary>A prompt carries no command, or no acceptance criteria.</summary>
    public const string IncompletePrompt = "KW-DOC-TASKS-004";

    /// <summary>A prompt tells the worker to open a plan, which no prompt may do.</summary>
    public const string PromptsAPlanRead = "KW-DOC-TASKS-005";

    /// <summary>A Ready plan has no task file beside it. Advisory: it never fails a merge.</summary>
    public const string MissingTaskFile = "KW-DOC-TASKS-006";

    /// <summary>The schema version this build reads.</summary>
    private const int SchemaVersion = 1;

    private const string PlanExtension = ".md";
    private const string TaskFileSuffix = ".tasks.json";

    private static readonly char[] WhitespaceSeparators = [' ', '\t', '\n', '\r'];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The names are pinned per property, so a document that misspells one loses that field
        // and fails to parse rather than being silently read as a different file.
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;

    /// <summary>Creates the validator over a repository root and its registry and ontology.</summary>
    /// <param name="repoRoot">The absolute repository root the plan paths are relative to.</param>
    /// <param name="config">The effective host configuration, with its ontology applied.</param>
    public PlanTaskFileValidator(string repoRoot, KyberWeaveConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        _repoRoot = repoRoot;
        _config = config;
    }

    /// <summary>One finding per disagreement between a plan and its task file, and per bad prompt.</summary>
    /// <param name="set">The loaded documents; the plans among them are the ones checked.</param>
    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new();
        IReadOnlyList<string> planningDirectories = PlanningDirectories();

        foreach (DocumentModel plan in set.Documents.Where(document =>
                     document.DocType == DocType.Plan
                     && !InventoryReachability.IsArchived(document.RelativePath)))
        {
            ValidatePlan(plan, planningDirectories, report);
        }

        return report;
    }

    /// <summary>
    /// The repository-relative directories holding plans and specifications: the folders of the
    /// three planning indexes.
    /// </summary>
    /// <remarks>
    /// Read from the registry rather than hard-coded, so a host that moves its plans is checked
    /// against the folder it actually uses. These are the same directories the Arbiter publishes
    /// as <c>config.planning-dirs</c>, which is what lets one detector answer both rules.
    /// </remarks>
    private IReadOnlyList<string> PlanningDirectories()
    {
        string[] properties =
        [
            ConfigRegConfig.PlanIndexProperty,
            ConfigRegConfig.SpecificationIndexProperty,
            ConfigRegConfig.TodoIndexProperty
        ];

        IReadOnlyList<ConfigRegEntry> registry = _config.ConfigReg.Resolve(_config.Ontology);
        List<string> directories = [];
        foreach (string property in properties)
        {
            string? indexPath = registry
                .FirstOrDefault(entry => string.Equals(entry.Name, property, StringComparison.Ordinal))
                ?.Path;
            if (string.IsNullOrWhiteSpace(indexPath))
            {
                continue;
            }

            string normalized = indexPath.Replace('\\', '/');
            int separator = normalized.LastIndexOf('/');
            if (separator > 0)
            {
                directories.Add(normalized[..separator]);
            }
        }

        return directories;
    }

    private void ValidatePlan(
        DocumentModel plan,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report)
    {
        PlanDocument parsed = PlanDocumentParser.Parse(plan.RawMarkdown);
        string taskFile = TaskFilePath(plan.RelativePath);

        if (!DocsRootPath.IsContainedFile(_repoRoot, taskFile, _config.Ontology.DocsRoots))
        {
            if (IsReady(plan))
            {
                report.Add(new Diagnostic(
                    MissingTaskFile,
                    Severity.Warning,
                    $"Plan '{plan.RelativePath}' is Ready and has no task file: the conductor has no prompt " +
                    $"to dispatch from {taskFile}.",
                    plan.RelativePath,
                    plan.RelativePath,
                    Hint: "Write the task file at finalization, with one prompt per task, or move the plan's " +
                    "status back until it is written."));
            }

            return;
        }

        if (!TryRead(taskFile, report, out PlanTaskFile? file))
        {
            return;
        }

        if (file!.SchemaVersion != SchemaVersion)
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' declares schema version {file.SchemaVersion}; this build reads " +
                $"version {SchemaVersion}.",
                plan.RelativePath,
                taskFile,
                Hint: "Regenerate the file with an architect that writes this schema version, rather than " +
                "reading a newer file as if its fields were this one's."));
            return;
        }

        if (string.IsNullOrWhiteSpace(file.Plan))
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' declares no plan, so nothing can tell which plan it was written from.",
                plan.RelativePath,
                taskFile,
                Hint: $"Add \"plan\": \"{plan.RelativePath}\"."));
            return;
        }

        if (!IsSamePath(file.Plan, plan.RelativePath))
        {
            report.Add(new Diagnostic(
                DiffersFromPlan,
                Severity.Error,
                $"Task file '{taskFile}' names plan '{file.Plan}' and sits beside '{plan.RelativePath}'.",
                plan.RelativePath,
                taskFile,
                Hint: "A task file belongs to the plan it sits beside; correct the plan path or move the file."));
        }

        CheckTasks(parsed, file, taskFile, report);
        CheckDigest(parsed, plan.RelativePath, file, taskFile, report);

        foreach (PlanTaskFileEntry? entry in file.Tasks ?? [])
        {
            if (entry is not null)
            {
                CheckPrompt(entry, taskFile, planningDirectories, report);
            }
        }
    }

    /// <summary>Reads and parses the file, reporting <see cref="Malformed"/> when either fails.</summary>
    private bool TryRead(string taskFile, DiagnosticReport report, out PlanTaskFile? file)
    {
        file = null;
        try
        {
            string json = File.ReadAllText(FullPath(taskFile));
            file = JsonSerializer.Deserialize<PlanTaskFile>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' is not the JSON this rule reads: {exception.Message}",
                taskFile,
                taskFile,
                Hint: "Regenerate the file rather than repairing it by hand: the digest below it is about to " +
                "be stale anyway."));
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' could not be read: {exception.Message}",
                taskFile,
                taskFile,
                Hint: "Make the file readable, then re-run docs validate."));
            return false;
        }

        if (file is not null)
        {
            return true;
        }

        report.Add(new Diagnostic(
            Malformed,
            Severity.Error,
            $"Task file '{taskFile}' is empty or holds no task-file object.",
            taskFile,
            taskFile,
            Hint: "Regenerate the file with the schema version, the plan path, the digest and one entry per task."));
        return false;
    }

    /// <summary>Reports each task, file scope and dependency on which the file disagrees with the plan.</summary>
    private static void CheckTasks(
        PlanDocument plan,
        PlanTaskFile file,
        string taskFile,
        DiagnosticReport report)
    {
        Dictionary<string, PlanTask> byId = new(StringComparer.Ordinal);
        foreach (PlanTask task in plan.Tasks)
        {
            byId[task.Id] = task;
        }

        Dictionary<string, PlanTaskFileEntry> declared = new(StringComparer.Ordinal);
        foreach (PlanTaskFileEntry? entry in file.Tasks ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || !declared.TryAdd(entry.Id, entry))
            {
                string id = entry?.Id ?? string.Empty;
                report.Add(new Diagnostic(
                    DiffersFromPlan,
                    Severity.Error,
                    $"Task file '{taskFile}' carries a task with no id, or two tasks sharing the id '{id}'.",
                    entry?.Id ?? taskFile,
                    taskFile,
                    Hint: "Give every entry the id of the plan task it was written from, once each."));
            }
        }

        foreach (PlanTask task in plan.Tasks.Where(task => !declared.ContainsKey(task.Id)))
        {
            report.Add(new Diagnostic(
                DiffersFromPlan,
                Severity.Error,
                $"Plan task '{task.Id}' has no entry in task file '{taskFile}', so the conductor cannot " +
                "dispatch it.",
                task.Id,
                taskFile,
                Hint: $"Add an entry for '{task.Id}' with its role, files, dependencies and prompt."));
        }

        foreach (string id in declared.Keys.Where(id => !byId.ContainsKey(id)))
        {
            report.Add(new Diagnostic(
                DiffersFromPlan,
                Severity.Error,
                $"Task file '{taskFile}' carries task '{id}', which the plan does not declare.",
                id,
                taskFile,
                Hint: "The plan is the authority: remove the entry, or add the task to the plan first."));
        }

        foreach ((string id, PlanTaskFileEntry entry) in declared)
        {
            if (!byId.TryGetValue(id, out PlanTask? task))
            {
                continue;
            }

            if (HasBlankElement(entry.Files) || HasBlankElement(entry.DependsOn))
            {
                report.Add(new Diagnostic(
                    DiffersFromPlan,
                    Severity.Error,
                    $"Task '{id}' carries an empty file or dependency entry in task file '{taskFile}': " +
                    "every entry must name a file or a task.",
                    id,
                    taskFile,
                    Hint: "Remove the empty entry, or copy the plan's file scope and dependencies."));
            }

            if (!SameSet(task.Files, entry.Files ?? []))
            {
                report.Add(new Diagnostic(
                    DiffersFromPlan,
                    Severity.Error,
                    $"Task '{id}' declares files {Describe(entry.Files ?? [])} in task file '{taskFile}' and " +
                    $"{Describe(task.Files)} in the plan.",
                    id,
                    taskFile,
                    Hint: "Copy the plan's file scope. A narrower scope in the file lets a worker be held to a " +
                    "scope it was never given, and a wider one authorizes files the plan did not approve."));
            }

            if (!SameSet(task.DependsOn, entry.DependsOn ?? []))
            {
                report.Add(new Diagnostic(
                    DiffersFromPlan,
                    Severity.Error,
                    $"Task '{id}' depends on {Describe(entry.DependsOn ?? [])} in task file '{taskFile}' and on " +
                    $"{Describe(task.DependsOn)} in the plan.",
                    id,
                    taskFile,
                    Hint: "Copy the plan's dependencies, so the conductor dispatches in the order the plan's graph " +
                    "declares."));
            }
        }
    }

    /// <summary>Reports a digest that no longer describes the plan the file sits beside.</summary>
    private static void CheckDigest(
        PlanDocument plan,
        string planPath,
        PlanTaskFile file,
        string taskFile,
        DiagnosticReport report)
    {
        string expected = PlanDocumentParser.ExecutionDigest(plan);
        if (string.Equals(file.ExecutionDigest, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The hint names the command that prints the digest rather than the digest itself: a
        // printed value would let someone repair a stale file without regenerating its prompts.
        report.Add(new Diagnostic(
            StaleDigest,
            Severity.Error,
            string.IsNullOrWhiteSpace(file.ExecutionDigest)
                ? $"Task file '{taskFile}' records no execution digest, so nothing shows it was written from this plan."
                : $"Task file '{taskFile}' records a stale execution digest: the plan moved after the file was written.",
            taskFile,
            taskFile,
            Hint: $"Regenerate the file's prompts from the plan as it now stands: run " +
            $"'kyber-weave arbiter plan \"{planPath}\" -f json' and copy its execution-digest into the file. " +
            "The digest covers the tasks, their text, files, dependencies and skills, the test contract rows, " +
            "the out-of-scope paths and the mode, so a status or decisions-row edit is not what moved it."));
    }

    /// <summary>Reports a prompt with no command, no acceptance criteria, or a plan read in it.</summary>
    private static void CheckPrompt(
        PlanTaskFileEntry entry,
        string taskFile,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report)
    {
        string id = string.IsNullOrWhiteSpace(entry.Id) ? taskFile : entry.Id;
        string prompt = entry.Prompt ?? string.Empty;

        if (string.IsNullOrWhiteSpace(prompt))
        {
            report.Add(new Diagnostic(
                IncompletePrompt,
                Severity.Error,
                $"Task '{id}' carries no prompt, so it carries neither a command nor acceptance criteria.",
                id,
                taskFile,
                Hint: "Write the prompt: the files with their symbols, the exact command, the full test class " +
                "names and the acceptance criteria."));
            return;
        }

        if (!HasCommand(prompt))
        {
            report.Add(new Diagnostic(
                IncompletePrompt,
                Severity.Error,
                $"Task '{id}' carries no command: nothing in its prompt runs.",
                id,
                taskFile,
                Hint: "Name the exact command the worker runs to verify the change, in backticks or a fenced " +
                "block, with a program and at least one argument."));
        }

        if (!HasAcceptanceCriteria(prompt))
        {
            report.Add(new Diagnostic(
                IncompletePrompt,
                Severity.Error,
                $"Task '{id}' carries no acceptance criteria: nothing in its prompt says how the worker knows " +
                "the change is done.",
                id,
                taskFile,
                Hint: "Add an 'Acceptance criteria:' line stating the observable result the command shows."));
        }

        IReadOnlyList<string> instructions = PlanReadInstruction.Find(prompt, planningDirectories);
        if (instructions.Count > 0)
        {
            report.Add(new Diagnostic(
                PromptsAPlanRead,
                Severity.Error,
                $"Task '{id}' tells the worker to open a plan: \"{instructions[0]}\".",
                id,
                taskFile,
                Hint: "Paste what the worker needs into the prompt instead. The read guard is advisory where a " +
                "harness's hooks are, so the prompt is the only place the ban can be kept."));
        }
    }

    /// <summary>
    /// True when the prompt runs something: a backticked span or a fenced block holding a program
    /// and at least one argument.
    /// </summary>
    /// <remarks>
    /// The narrow reading is deliberate. A single backticked token is a name — a path, a class, a
    /// setting — and prose that mentions tests does not run them; this repository shipped prompts
    /// whose runner matched no test class, which is the failure a bare mention hides.
    /// </remarks>
    private static bool HasCommand(string prompt)
    {
        foreach (Match match in FencedBlockRegex().Matches(prompt))
        {
            if (LooksLikeCommand(match.Groups["body"].Value))
            {
                return true;
            }
        }

        foreach (Match match in InlineSpanRegex().Matches(prompt))
        {
            if (LooksLikeCommand(match.Groups["body"].Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeCommand(string value) =>
        value.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    /// <summary>
    /// True when the prompt states acceptance criteria: text after an <c>Acceptance criteria</c>
    /// label, on its line or in the indented block below it.
    /// </summary>
    private static bool HasAcceptanceCriteria(string prompt)
    {
        string[] lines = prompt.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            Match match = AcceptanceLabelRegex().Match(lines[index]);
            if (!match.Success)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(match.Groups["text"].Value))
            {
                return true;
            }

            // A label on its own line takes the criteria below it, but only an indented or
            // bulleted block: the next paragraph is the next thought, not the criteria.
            for (int next = index + 1; next < lines.Length; next++)
            {
                if (string.IsNullOrWhiteSpace(lines[next]))
                {
                    continue;
                }

                string trimmed = lines[next].TrimStart();
                if (trimmed.Length != lines[next].Length
                    || trimmed.StartsWith("- ", StringComparison.Ordinal)
                    || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    return true;
                }

                break;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the plan declares itself Ready, which is the state its task file is written in.
    /// </summary>
    /// <remarks>
    /// Read from the body rather than the frontmatter, because this repository's plan vocabulary
    /// has no Ready status: an approved plan stays <c>status: current</c> and says
    /// <c>**Status: Ready**</c> in its opening paragraph.
    /// </remarks>
    private static bool IsReady(DocumentModel plan) => ReadyStatusRegex().IsMatch(plan.Body);

    private static string TaskFilePath(string planRelativePath) =>
        planRelativePath.EndsWith(PlanExtension, StringComparison.OrdinalIgnoreCase)
            ? planRelativePath[..^PlanExtension.Length] + TaskFileSuffix
            : planRelativePath + TaskFileSuffix;

    private string FullPath(string relativePath) =>
        Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsSamePath(string left, string right) =>
        DocsRootPath.PathComparer.Equals(
            left.Replace('\\', '/').Trim('/'),
            right.Replace('\\', '/').Trim('/'));

    private static bool SameSet(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    /// <summary>
    /// Whether a file scope or dependency list carries an entry that names nothing: a null or
    /// blank element the JSON shape allows but no plan task declares. Normalization drops such
    /// entries, so without this check <c>files:[null]</c> compares equal to <c>files:[]</c> and
    /// junk passes as a matching scope.
    /// </summary>
    private static bool HasBlankElement(IReadOnlyList<string>? values) =>
        values is not null && values.Any(string.IsNullOrWhiteSpace);

    private static string Normalize(IReadOnlyList<string> values) =>
        string.Join(
            '\n',
            values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().Replace('\\', '/'))
                .Order(StringComparer.Ordinal));

    private static string Describe(IReadOnlyList<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    [GeneratedRegex(@"```[^\n]*\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FencedBlockRegex();

    [GeneratedRegex(@"`(?<body>[^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSpanRegex();

    [GeneratedRegex(
        @"^[ \t]*(?:[-*+][ \t]+)?\**acceptance criteria\**\s*:[ \t]*(?<text>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcceptanceLabelRegex();

    [GeneratedRegex(@"\*\*Status:\s*Ready\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadyStatusRegex();
}
