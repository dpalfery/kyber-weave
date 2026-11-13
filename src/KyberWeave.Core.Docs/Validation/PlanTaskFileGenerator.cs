using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>What <see cref="PlanTaskFileGenerator.Run"/> does with the file it derives.</summary>
public enum PlanTaskFileMode
{
    /// <summary>Derive the file and report it; touch nothing.</summary>
    Preview,

    /// <summary>Derive the file and write it beside the plan, unless it is already exactly that.</summary>
    Write,

    /// <summary>Derive the file and report every way the one on disk differs; touch nothing.</summary>
    Check
}

/// <summary>The result of one <see cref="PlanTaskFileGenerator.Run"/>.</summary>
/// <param name="Report">The findings: gaps in the plan, differences found by a check, or problems writing.</param>
/// <param name="TaskFilePath">The repository-relative path of the task file beside the plan.</param>
/// <param name="Json">The derived file's text; null when the plan has a gap that stops it being derived.</param>
/// <param name="ExecutionDigest">The digest recorded in the derived file; null when none was derived.</param>
/// <param name="TaskCount">How many tasks the derived file carries.</param>
/// <param name="Wrote">True when this run changed the file on disk.</param>
/// <param name="File">The derived file; null when none was derived.</param>
public sealed record PlanTaskFileOutcome(
    DiagnosticReport Report,
    string TaskFilePath,
    string? Json,
    string? ExecutionDigest,
    int TaskCount,
    bool Wrote,
    PlanTaskFile? File = null);

/// <summary>
/// Derives a plan's, or a specification's, task file from the task list it sits beside, so nobody
/// writes the machine format by hand.
/// </summary>
/// <remarks>
/// <para>
/// The task file restates the plan for the conductor: the file scope, dependencies and skills come
/// from <see cref="PlanDocumentParser"/>, the digest is <see cref="PlanDocumentParser.ExecutionDigest"/>,
/// and each prompt is the plan task's own text plus the one command and the acceptance criteria the
/// validator requires, taken from the task or from its row of the test contract. Nothing in the file
/// is authored, so it is rewritten whole and a second run changes nothing.
/// </para>
/// <para>
/// Where the plan lacks something a prompt needs, the run reports the task and the label to add, with
/// an example line, and writes nothing: a half-built file would fail validation the same way, later.
/// </para>
/// </remarks>
public sealed partial class PlanTaskFileGenerator
{
    /// <summary>The plan carries no Tasks section, or none of its tasks parsed.</summary>
    public const string NoTasks = "KW-ARB-PLAN-001";

    /// <summary>The plan file is missing, unreadable, or not a file this command writes beside.</summary>
    public const string PlanNotReadable = "KW-ARB-PLAN-002";

    private static readonly HashSet<string> Runners = new(StringComparer.Ordinal)
    {
        "dotnet", "npm", "npx", "pnpm", "yarn", "cargo", "go", "pytest", "python", "python3", "node", "make",
        "bash", "sh", "pwsh", "kyber-weave", "scripts/proc-guard.sh"
    };

    private const string WorktreeSlot = "{worktree}";
    private const string PriorResultsSlot = "{prior_results}";

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;

    /// <summary>Creates the generator over a repository root and its effective configuration.</summary>
    /// <param name="repoRoot">The absolute repository root the artifact path is relative to.</param>
    /// <param name="config">The effective host configuration, with its ontology applied.</param>
    public PlanTaskFileGenerator(string repoRoot, KyberWeaveConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);

        _repoRoot = repoRoot;
        _config = config;
    }

    /// <summary>The task file's repository-relative path for the plan or specification task list at <paramref name="artifactPath"/>.</summary>
    /// <param name="artifactPath">The plan, or a specification's <c>tasks.md</c>, repository-relative.</param>
    public static string TaskFilePathOf(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        string normalized = artifactPath.Replace('\\', '/');
        return PlanTaskFileValidator.TaskFilePathFor(normalized, PlanTaskFileValidator.IsSpecTaskListPath(normalized));
    }

    /// <summary>Derives the task file for one plan or specification task list and writes, checks or previews it.</summary>
    /// <param name="artifactPath">The plan, or a specification's <c>tasks.md</c>, repository-relative.</param>
    /// <param name="mode">What to do with the derived file.</param>
    public PlanTaskFileOutcome Run(string artifactPath, PlanTaskFileMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        string artifact = artifactPath.Replace('\\', '/').Trim('/');
        string taskFile = TaskFilePathOf(artifact);
        DiagnosticReport report = new();

        string? markdown = ReadArtifact(artifact, report);
        if (markdown is null)
        {
            return new PlanTaskFileOutcome(report, taskFile, null, null, 0, false);
        }

        IReadOnlyList<string> planningDirectories = PlanTaskFileValidator.PlanningDirectories(
            _config.ConfigReg.Resolve(_config.Ontology));
        PlanTaskFile? file = Build(artifact, markdown, planningDirectories, report, _repoRoot);
        if (file is null)
        {
            return new PlanTaskFileOutcome(report, taskFile, null, null, 0, false);
        }

        string json = PlanTaskFileJson.Serialize(file);
        int count = file.Tasks?.Count ?? 0;
        bool wrote = false;

        switch (mode)
        {
            case PlanTaskFileMode.Check:
                CompareWithDisk(artifact, taskFile, file, json, report);
                break;
            case PlanTaskFileMode.Write:
                wrote = WriteIfChanged(artifact, taskFile, json, report);
                break;
            case PlanTaskFileMode.Preview:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown task file mode.");
        }

        return new PlanTaskFileOutcome(report, taskFile, json, file.ExecutionDigest, count, wrote, file);
    }

    /// <summary>
    /// Derives the task file from a plan's text. Returns null, with the reasons in
    /// <paramref name="report"/>, when the plan lacks something the file needs.
    /// </summary>
    /// <param name="artifactPath">The plan's repository-relative path, recorded in the file.</param>
    /// <param name="markdown">The plan, or specification task list, as authored.</param>
    /// <param name="planningDirectories">The folders holding plans, which no prompt may send a worker into.</param>
    /// <param name="report">Receives every gap found; all of them are reported in one run.</param>
    /// <param name="repoRoot">The repository root, to tell production files that exist from ones a task must create; null when unknown.</param>
    public static PlanTaskFile? Build(
        string artifactPath,
        string markdown,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report,
        string? repoRoot = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(planningDirectories);
        ArgumentNullException.ThrowIfNull(report);

        PlanDocument parsed = PlanDocumentParser.Parse(markdown);
        bool gaps = false;

        foreach (Diagnostic finding in parsed.Diagnostics)
        {
            // An unknown dependency token is dropped by the parser, so a file derived over it would
            // describe a plan that does not say what it says; the missing-files finding is a warning.
            bool dropped = finding.Code == PlanDocumentParser.UnknownDependencyRule;
            report.Add(dropped ? finding with { Severity = Severity.Error } : finding);
            gaps |= dropped;
        }

        if (parsed.Tasks.Count == 0)
        {
            report.Add(new Diagnostic(
                NoTasks,
                Severity.Error,
                parsed.HasTasks
                    ? $"'{artifactPath}' has a Tasks section, but no task in it parsed, so there is nothing to write a task file from."
                    : $"'{artifactPath}' has no Tasks section, so there is nothing to write a task file from.",
                artifactPath,
                artifactPath,
                Hint: "Add a heading per task under '## Tasks', for example '### T1: Add the validator', with a " +
                "'Files:' line under it."));
            return null;
        }

        PlanPacketContext context = PlanPacketContext.Parse(markdown);
        PlanFileScope fileScope = new(repoRoot);
        List<PlanTaskFileEntry> entries = [];
        foreach (PlanTask task in parsed.Tasks)
        {
            PlanContractRow? row = parsed.ContractRows.FirstOrDefault(
                candidate => string.Equals(candidate.TaskId, task.Id, StringComparison.Ordinal));
            PlanTaskFileEntry? entry = BuildEntry(task, row, parsed, context, repoRoot, fileScope, artifactPath, planningDirectories, report);
            if (entry is null)
            {
                gaps = true;
            }
            else
            {
                entries.Add(entry);
            }
        }

        return gaps
            ? null
            : new PlanTaskFile(
                1,
                artifactPath,
                PlanDocumentParser.ExecutionDigest(parsed),
                entries);
    }

    private static PlanTaskFileEntry? BuildEntry(
        PlanTask task,
        PlanContractRow? row,
        PlanDocument parsed,
        PlanPacketContext context,
        string? repoRoot,
        PlanFileScope fileScope,
        string artifactPath,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report)
    {
        string body = task.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        string[] lines = body.Split('\n');
        bool gap = false;

        string? role = LabelValue(lines, "role", "agent", "specialist") ?? (task.Skills.Count > 0 ? task.Skills[0] : null);
        if (role is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.IncompletePrompt,
                Severity.Error,
                $"Task '{task.Id}' names no agent or skill, so the task file cannot say who runs it.",
                task.Id,
                artifactPath,
                Hint: $"Under task '{task.Id}' in the plan add a line such as 'Required skills: `csharp-dev`', " +
                $"then run {TaskFileCommand.Write(artifactPath)} again."));
            gap = true;
        }

        string session = LabelValue(lines, "session", "session group") ?? task.Id;

        string? command = null;
        if (!PlanTaskFileValidator.HasCommand(body))
        {
            command = CommandFromContract(row);
            if (command is null)
            {
                report.Add(new Diagnostic(
                    PlanTaskFileValidator.IncompletePrompt,
                    Severity.Error,
                    $"Task '{task.Id}' gives no command to run: no backticked command with a program and an " +
                    "argument in its text, and none in the 'Runner filter' or 'Runner command' cell of its row in the test contract.",
                    task.Id,
                    artifactPath,
                    Hint: $"Under task '{task.Id}' in the plan add a line such as " +
                    "'Command: `dotnet test --filter FullyQualifiedName~MyTests`', " +
                    $"then run {TaskFileCommand.Write(artifactPath)} again."));
                gap = true;
            }
        }

        string? acceptance = null;
        if (!PlanTaskFileValidator.HasAcceptanceCriteria(body))
        {
            acceptance = AcceptanceFromTask(lines) ?? AcceptanceFromContract(row);
            if (acceptance is null)
            {
                report.Add(new Diagnostic(
                    PlanTaskFileValidator.IncompletePrompt,
                    Severity.Error,
                    $"Task '{task.Id}' states no acceptance criteria: no 'Acceptance criteria:' line (or " +
                    "'Acceptance:', 'Done when:', 'Definition of done:', 'Success criteria:') in its text, and " +
                    "nothing in the 'GREEN acceptance' or 'Observable behaviour' cell of its row in the test contract.",
                    task.Id,
                    artifactPath,
                    Hint: $"Under task '{task.Id}' in the plan add a line such as " +
                    "'Acceptance criteria: the class reports a non-zero total with no failures.', " +
                    $"then run {TaskFileCommand.Write(artifactPath)} again."));
                gap = true;
            }
        }

        string fixCommand = TaskFileCommand.Write(artifactPath);
        InlinedPlanContent inlined = PlanPacketInliner.Build(task, body, context, artifactPath, fixCommand);
        report.AddRange(inlined.Findings);
        if (PlanCompileUnitOrder.Check(task, parsed, repoRoot, artifactPath, fixCommand) is { } order)
        {
            report.Add(order);
        }

        string prompt = ComposePrompt(task, body, inlined.Text, command, acceptance);
        IReadOnlyList<string> instructions = PlanReadInstruction.Find(prompt, planningDirectories);
        if (instructions.Count > 0)
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.PromptsAPlanRead,
                Severity.Error,
                $"Task '{task.Id}' tells the worker to open a plan: \"{instructions[0]}\".",
                task.Id,
                artifactPath,
                Hint: $"Remove that instruction from task '{task.Id}' in the plan, and put what the worker needs " +
                $"in the task text, then run {TaskFileCommand.Write(artifactPath)} again."));
            gap = true;
        }

        (IReadOnlyList<string> keptFiles, IReadOnlyList<string> droppedFiles) = fileScope.Filter(task.Files);
        if (droppedFiles.Count > 0)
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.NotARepositoryPath,
                Severity.Info,
                $"Task '{task.Id}': left out of its files because they are not repository paths: {string.Join(", ", droppedFiles)}.",
                task.Id,
                artifactPath,
                Hint: "A file is kept when it exists, or when its top-level folder exists and its extension is one the repository tracks. "
                      + "List a file the task creates as its repository-relative path in the task's Files line."));
        }

        return gap
            ? null
            : new PlanTaskFileEntry(
                task.Id,
                role,
                [.. task.DependsOn],
                [.. keptFiles],
                session,
                prompt);
    }

    private static string ComposePrompt(PlanTask task, string body, string inlined, string? command, string? acceptance)
    {
        StringBuilder prompt = new();
        prompt.Append("Task ").Append(task.Id);
        if (task.Title.Length > 0)
        {
            prompt.Append(": ").Append(task.Title);
        }

        prompt.Append('\n').Append("Worktree: ").Append(WorktreeSlot).Append("\n\n").Append(body).Append('\n');
        if (inlined.Length > 0)
        {
            prompt.Append('\n').Append(inlined);
        }

        if (command is not null)
        {
            prompt.Append("\nCommand: `").Append(command).Append("`\n");
        }

        if (acceptance is not null)
        {
            prompt.Append('\n').Append(acceptance).Append('\n');
        }

        prompt.Append("\nResults of the tasks this one follows, as their workers reported them:\n")
            .Append(PriorResultsSlot).Append('\n');
        return prompt.ToString();
    }

    /// <summary>
    /// A command from the task's row of the test contract: a backticked span with a program and an argument, else
    /// the whole cell when it starts with a program that runs tests. Prose in the cell, such as "manual check",
    /// is not a command.
    /// </summary>
    private static string? CommandFromContract(PlanContractRow? row)
    {
        foreach (string? cell in new[] { row?.RunnerFilter, row?.TestProjectOrFile })
        {
            if (string.IsNullOrWhiteSpace(cell))
            {
                continue;
            }

            foreach (Match span in BacktickSpanRegex().Matches(cell))
            {
                string candidate = span.Groups["body"].Value.Trim();
                if (PlanTaskFileValidator.HasCommand("`" + candidate + "`"))
                {
                    return candidate;
                }
            }

            string whole = WhitespaceRegex().Replace(cell.Replace("`", string.Empty, StringComparison.Ordinal), " ").Trim();
            string[] words = whole.Split(' ');
            if (words.Length >= 2 && (Runners.Contains(words[0]) || words[0].Contains('/', StringComparison.Ordinal)))
            {
                return whole;
            }
        }

        return null;
    }

    private static string? AcceptanceFromContract(PlanContractRow? row)
    {
        foreach (string? cell in new[] { row?.GreenAcceptance, row?.ObservableBehaviour })
        {
            if (!string.IsNullOrWhiteSpace(cell))
            {
                return "Acceptance criteria: " + WhitespaceRegex().Replace(cell, " ").Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The task text's acceptance statement under any of the labels an author naturally writes, restated under the
    /// one label the validator reads: the text on the label's line, then any bullets or indented lines below it.
    /// </summary>
    private static string? AcceptanceFromTask(string[] lines)
    {
        for (int index = 0; index < lines.Length; index++)
        {
            string plain = lines[index].Replace("*", string.Empty, StringComparison.Ordinal);
            Match label = AcceptanceLabelRegex().Match(plain);
            if (!label.Success)
            {
                continue;
            }

            int indent = plain.Length - plain.TrimStart().Length;
            bool labelIsBullet = plain.TrimStart().StartsWith("- ", StringComparison.Ordinal)
                || plain.TrimStart().StartsWith("+ ", StringComparison.Ordinal);
            List<string> items = [];
            for (int next = index + 1; next < lines.Length; next++)
            {
                string line = lines[next].Replace("*", string.Empty, StringComparison.Ordinal);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string trimmed = line.TrimStart();
                int lineIndent = line.Length - trimmed.Length;
                bool bullet = trimmed.StartsWith("- ", StringComparison.Ordinal);
                if (lineIndent > indent || (!labelIsBullet && bullet && lineIndent == indent))
                {
                    items.Add(bullet ? trimmed : "- " + trimmed);
                    continue;
                }

                break;
            }

            string inline = label.Groups["text"].Value.Trim();
            if (inline.Length == 0 && items.Count == 0)
            {
                continue;
            }

            StringBuilder result = new("Acceptance criteria:");
            if (inline.Length > 0)
            {
                result.Append(' ').Append(inline);
            }

            foreach (string item in items)
            {
                result.Append('\n').Append(item);
            }

            return result.ToString();
        }

        return null;
    }

    /// <summary>The text after the first of <paramref name="names"/> used as a label in the task text, without backticks.</summary>
    private static string? LabelValue(string[] lines, params string[] names)
    {
        foreach (string line in lines)
        {
            Match match = SimpleLabelRegex().Match(line.Replace("*", string.Empty, StringComparison.Ordinal));
            if (!match.Success
                || !names.Contains(WhitespaceRegex().Replace(match.Groups["label"].Value, " "), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = match.Groups["text"].Value.Trim();
            Match span = BacktickSpanRegex().Match(value);
            string chosen = span.Success ? span.Groups["body"].Value.Trim() : value.Split(' ', 2)[0].Trim('`', '.', ',');
            if (chosen.Length > 0)
            {
                return chosen;
            }
        }

        return null;
    }

    private string? ReadArtifact(string artifact, DiagnosticReport report)
    {
        string full = Path.Combine(_repoRoot, artifact.Replace('/', Path.DirectorySeparatorChar));
        if (!artifact.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            report.Add(new Diagnostic(
                PlanNotReadable,
                Severity.Error,
                $"'{artifact}' is not a plan or specification task list file that exists in this repository.",
                artifact,
                artifact,
                Hint: "Pass the plan file, for example 'kyber-weave docs tasks docs/plans/2026-11-12-my-plan.md --write', " +
                "or a specification's tasks.md."));
            return null;
        }

        try
        {
            return File.ReadAllText(full);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                PlanNotReadable,
                Severity.Error,
                $"'{artifact}' could not be read: {exception.Message}",
                artifact,
                artifact));
            return null;
        }
    }

    private bool WriteIfChanged(string artifact, string taskFile, string json, DiagnosticReport report)
    {
        string full = Path.Combine(_repoRoot, taskFile.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (new FileInfo(full).LinkTarget is not null
                || !DocsRootPath.IsContainedFile(_repoRoot, artifact, _config.Ontology.DocsRoots))
            {
                report.Add(new Diagnostic(
                    PlanTaskFileValidator.Malformed,
                    Severity.Error,
                    $"'{taskFile}' is a symbolic link, or its plan is outside the documentation roots, so it is not written.",
                    taskFile,
                    taskFile,
                    Hint: "Remove the link, or pass a plan inside the documentation roots."));
                return false;
            }

            if (File.Exists(full) && string.Equals(File.ReadAllText(full), json, StringComparison.Ordinal))
            {
                return false;
            }

            File.WriteAllText(full, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.Malformed,
                Severity.Error,
                $"'{taskFile}' could not be written: {exception.Message}",
                taskFile,
                taskFile));
            return false;
        }
    }

    private void CompareWithDisk(string artifact, string taskFile, PlanTaskFile expected, string expectedJson, DiagnosticReport report)
    {
        string full = Path.Combine(_repoRoot, taskFile.Replace('/', Path.DirectorySeparatorChar));
        string fix = TaskFileCommand.Write(artifact);
        if (!File.Exists(full))
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.MissingTaskFile,
                Severity.Error,
                $"There is no task file at '{taskFile}'.",
                taskFile,
                taskFile,
                Hint: $"Run {fix}."));
            return;
        }

        string actualText;
        PlanTaskFile? actual;
        try
        {
            actualText = File.ReadAllText(full);
            actual = JsonSerializer.Deserialize<PlanTaskFile>(actualText, PlanTaskFileJson.ReadOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.Malformed,
                Severity.Error,
                $"The task file '{taskFile}' cannot be read: {exception.Message}",
                taskFile,
                taskFile,
                Hint: $"Run {fix}."));
            return;
        }

        if (actual is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileValidator.Malformed,
                Severity.Error,
                $"The task file '{taskFile}' is empty.",
                taskFile,
                taskFile,
                Hint: $"Run {fix}."));
            return;
        }

        int before = report.Items.Count;
        if (actual.SchemaVersion != expected.SchemaVersion)
        {
            Differs(report, taskFile, $"schema-version is {actual.SchemaVersion}, the plan produces {expected.SchemaVersion}", fix, PlanTaskFileValidator.Malformed);
        }

        if (!string.Equals(actual.Plan, expected.Plan, StringComparison.Ordinal))
        {
            Differs(report, taskFile, $"plan is '{actual.Plan}', expected '{expected.Plan}'", fix);
        }

        if (!string.Equals(actual.ExecutionDigest, expected.ExecutionDigest, StringComparison.OrdinalIgnoreCase))
        {
            Differs(report, taskFile, "execution-digest is stale: the plan moved after the file was written", fix, PlanTaskFileValidator.StaleDigest);
        }

        CompareTasks(actual, expected, taskFile, fix, report);

        if (report.Items.Count == before && !string.Equals(actualText, expectedJson, StringComparison.Ordinal))
        {
            Differs(report, taskFile, "the content is the same but the file is not in the form the command writes (formatting)", fix);
        }
    }

    private static void CompareTasks(PlanTaskFile actual, PlanTaskFile expected, string taskFile, string fix, DiagnosticReport report)
    {
        Dictionary<string, PlanTaskFileEntry> have = [];
        foreach (PlanTaskFileEntry? entry in actual.Tasks ?? [])
        {
            if (entry?.Id is { } id)
            {
                have[id] = entry;
            }
        }

        foreach (PlanTaskFileEntry want in expected.Tasks ?? [])
        {
            if (!have.TryGetValue(want.Id!, out PlanTaskFileEntry? got))
            {
                Differs(report, taskFile, $"task '{want.Id}' is missing", fix);
                continue;
            }

            foreach ((string field, bool same) in new (string, bool)[]
                     {
                         ("role", got.Role == want.Role),
                         ("session", got.Session == want.Session),
                         ("files", (got.Files ?? []).SequenceEqual(want.Files ?? [])),
                         ("depends-on", (got.DependsOn ?? []).SequenceEqual(want.DependsOn ?? [])),
                         ("prompt", string.Equals(got.Prompt, want.Prompt, StringComparison.Ordinal))
                     })
            {
                if (!same)
                {
                    Differs(report, taskFile, $"task '{want.Id}' differs in {field}", fix);
                }
            }
        }

        HashSet<string> wanted = [.. (expected.Tasks ?? []).Select(entry => entry.Id!)];
        foreach (string extra in have.Keys.Where(id => !wanted.Contains(id)))
        {
            Differs(report, taskFile, $"task '{extra}' is in the file but not in the plan", fix);
        }
    }

    private static void Differs(DiagnosticReport report, string taskFile, string what, string fix, string rule = PlanTaskFileValidator.DiffersFromPlan) =>
        report.Add(new Diagnostic(
            rule,
            Severity.Error,
            $"'{taskFile}' is not what the plan produces: {what}.",
            taskFile,
            taskFile,
            Hint: $"Run {fix}. Do not edit the file by hand."));

    [GeneratedRegex(@"`(?<body>[^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickSpanRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"^[ \t]*(?:[-+][ \t]+)?(?<label>[A-Za-z][A-Za-z ]*?)[ \t]*:[ \t]*(?<text>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SimpleLabelRegex();

    [GeneratedRegex(
        @"^[ \t]*(?:[-+][ \t]+)?(?<label>acceptance(?:[ \t]+criteria)?|done[ \t]+when|definition[ \t]+of[ \t]+done|success[ \t]+criteria|exit[ \t]+criteria)[ \t]*:[ \t]*(?<text>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcceptanceLabelRegex();
}
