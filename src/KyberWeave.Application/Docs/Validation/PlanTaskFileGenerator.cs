using KyberWeave.Contracts.Repositories;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Derives a plan's, or a specification's, task file from the task list it sits beside, so nobody
/// writes the machine format by hand.
/// </summary>
/// <remarks>
/// <para>
/// The task file restates the plan for the conductor: the file scope, dependencies and capabilities come
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
        "bash", "sh", "pwsh", "kyber-weave", "scripts/fast-loop.sh"
    };

    private const string WorktreeSlot = "{worktree}";
    private const string PriorResultsSlot = "{prior_results}";

    private readonly string _repoRoot;
    private readonly KyberWeaveConfig _config;
    private readonly IRepositoryFileStore _files;
    private readonly IPlanTaskAgentPicker? _picker;

    /// <summary>Creates the generator with a picker that chooses each unassigned task's agent when the file is written.</summary>
    /// <param name="repoRoot">The absolute repository root the artifact path is relative to.</param>
    /// <param name="config">The effective host configuration, with its ontology applied.</param>
    /// <param name="files">Reads the plan and the task file and writes the task file.</param>
    /// <param name="picker">The agent picker, or null when no decision model is configured.</param>
    public PlanTaskFileGenerator(string repoRoot, KyberWeaveConfig config, IRepositoryFileStore files, IPlanTaskAgentPicker? picker = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(files);

        _repoRoot = repoRoot;
        _config = config;
        _files = files;
        _picker = picker;
    }

    /// <summary>The task file's repository-relative path for the plan or specification task list at <paramref name="artifactPath"/>.</summary>
    /// <param name="artifactPath">The plan, or a specification's <c>tasks.md</c>, repository-relative.</param>
    public static string TaskFilePathOf(string artifactPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        string normalized = artifactPath.Replace('\\', '/');
        return PlanTaskFileRules.TaskFilePathFor(normalized, PlanTaskFileRules.IsSpecTaskListPath(normalized));
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

        IReadOnlyList<string> planningDirectories = PlanTaskFileRules.PlanningDirectories(
            _config.ConfigReg.Resolve(_config.Ontology));
        PlanTaskFile? file = Build(artifact, markdown, planningDirectories, report, _config.WorkerRoster);
        if (file is null)
        {
            return new PlanTaskFileOutcome(report, taskFile, null, null, 0, false);
        }

        file = WithAgents(file, taskFile, mode, markdown, artifact, report, out IReadOnlyList<string> difficultyChecks);
        string yaml = PlanTaskFileFormat.Serialize(file);
        int count = file.Tasks?.Count ?? 0;
        bool wrote = false;

        switch (mode)
        {
            case PlanTaskFileMode.Check:
                CompareWithDisk(artifact, taskFile, file, yaml, report);
                break;
            case PlanTaskFileMode.Write:
                wrote = WriteIfChanged(artifact, taskFile, yaml, report);
                break;
            case PlanTaskFileMode.Convert:
                wrote = ConvertLegacy(artifact, taskFile, yaml, report);
                break;
            case PlanTaskFileMode.Preview:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown task file mode.");
        }

        return new PlanTaskFileOutcome(report, taskFile, yaml, file.ExecutionDigest, count, wrote, file, difficultyChecks);
    }

    /// <summary>
    /// Derives the task file from a plan's text. Returns null, with the reasons in
    /// <paramref name="report"/>, when the plan lacks something the file needs.
    /// </summary>
    /// <param name="artifactPath">The plan's repository-relative path, recorded in the file.</param>
    /// <param name="markdown">The plan, or specification task list, as authored.</param>
    /// <param name="planningDirectories">The folders holding plans, which no prompt may send a worker into.</param>
    /// <param name="report">Receives every gap found; all of them are reported in one run.</param>
    /// <param name="workerRoster">The roster the file records; defaults to <c>technology</c>.</param>
    public static PlanTaskFile? Build(
        string artifactPath,
        string markdown,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report,
        string? workerRoster = null)
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
        List<PlanTaskFileEntry> entries = [];
        foreach (PlanTask task in parsed.Tasks)
        {
            PlanContractRow? row = parsed.ContractRows.FirstOrDefault(
                candidate => string.Equals(candidate.TaskId, task.Id, StringComparison.Ordinal));
            PlanTaskFileEntry? entry = BuildEntry(task, row, context, artifactPath, planningDirectories, report);
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
                PlanTaskFileRules.SchemaVersion,
                artifactPath,
                PlanDocumentParser.ExecutionDigest(parsed),
                entries,
                workerRoster ?? WorkerRosters.Tiered);
    }

    private static PlanTaskFileEntry? BuildEntry(
        PlanTask task,
        PlanContractRow? row,
        PlanPacketContext context,
        string artifactPath,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report)
    {
        string body = task.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        string[] lines = body.Split('\n');
        bool gap = false;

        // The plan says what the task needs; the conductor picks the agent. An explicit role label is the owner's override.
        string? role = LabelValue(lines, "role", "agent", "specialist");
        string? capabilities = task.Capabilities ?? (task.Skills.Count > 0 ? string.Join(", ", task.Skills) : null);
        if (role is null && capabilities is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.IncompletePrompt,
                Severity.Error,
                $"Task '{task.Id}' says nothing about the abilities its work needs, so the conductor cannot choose an agent for it.",
                task.Id,
                artifactPath,
                Hint: $"Under task '{task.Id}' in the plan add a line such as " +
                "'Capabilities: C# ASP.NET API controllers and services, with xUnit tests', " +
                $"then run {TaskFileCommand.Write(artifactPath)} again."));
            gap = true;
        }

        string session = LabelValue(lines, "session", "session group") ?? task.Id;

        // A task that names no difficulty is medium by default; the plan's own line is the architect's rating.
        bool rated = !string.IsNullOrWhiteSpace(task.Difficulty);
        string difficulty = rated ? task.Difficulty! : "medium";
        string difficultySource = rated ? "architect" : "default";
        string? technology = string.IsNullOrWhiteSpace(task.Technology) ? null : task.Technology;

        string? command = null;
        if (!PlanTaskFileRules.HasCommand(body))
        {
            command = CommandFromContract(row);
            if (command is null)
            {
                report.Add(new Diagnostic(
                    PlanTaskFileRules.IncompletePrompt,
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
        if (!PlanTaskFileRules.HasAcceptanceCriteria(body))
        {
            acceptance = AcceptanceFromTask(lines) ?? AcceptanceFromContract(row);
            if (acceptance is null)
            {
                report.Add(new Diagnostic(
                    PlanTaskFileRules.IncompletePrompt,
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
        string prompt = ComposePrompt(task, body, inlined.Text, command, acceptance);
        IReadOnlyList<string> instructions = PlanReadInstruction.Find(prompt, planningDirectories);
        if (instructions.Count > 0)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.PromptsAPlanRead,
                Severity.Error,
                $"Task '{task.Id}' tells the worker to open a plan: \"{instructions[0]}\".",
                task.Id,
                artifactPath,
                Hint: $"Remove that instruction from task '{task.Id}' in the plan, and put what the worker needs " +
                $"in the task text, then run {TaskFileCommand.Write(artifactPath)} again."));
            gap = true;
        }

        (IReadOnlyList<string> keptFiles, IReadOnlyList<string> scratchFiles, IReadOnlyList<string> droppedFiles) = PlanFileScope.Filter(task.Files);
        if (scratchFiles.Count > 0)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.ScratchPathInFiles,
                Severity.Error,
                $"Task '{task.Id}' lists scratch paths as its files: {string.Join(", ", scratchFiles)}. The scratch folder is " +
                "git-ignored and every worker may write there, so it is never part of a task's file scope.",
                task.Id,
                artifactPath,
                Hint: $"Take those entries out of the Files list of task '{task.Id}' in the plan. If the worker must write " +
                "there, say so in the task text, outside the Files list, then run " +
                $"{TaskFileCommand.Write(artifactPath)} again."));
            gap = true;
        }

        if (droppedFiles.Count > 0)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.NotARepositoryPath,
                Severity.Info,
                $"Task '{task.Id}': left out of its files because they are not repository paths: {string.Join(", ", droppedFiles)}.",
                task.Id,
                artifactPath,
                Hint: "An entry is kept when its text is a repository-relative path: no scheme, call, quote or space, no leading slash, "
                      + "and a folder name before any wildcard. Whether the path exists yet makes no difference."));
        }

        return gap
            ? null
            : new PlanTaskFileEntry(
                task.Id,
                role,
                capabilities,
                [.. task.DependsOn],
                [.. keptFiles],
                session,
                prompt,
                Difficulty: difficulty,
                DifficultySource: difficultySource,
                Technology: technology);
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
    internal static string? CommandFromContract(PlanContractRow? row)
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
                if (PlanTaskFileRules.HasCommand("`" + candidate + "`"))
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
        if (!artifact.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !_files.FileExists(full))
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
            if (_files.GetLinkTarget(full) is not null)
            {
                report.Add(new Diagnostic(
                    PlanNotReadable,
                    Severity.Error,
                    $"'{artifact}' is a symbolic link, so it is not read.",
                    artifact,
                    artifact,
                    Hint: "Pass the plan file itself, not a link to it."));
                return null;
            }

            return _files.ReadAllTextBounded(full);
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidDataException)
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

    /// <summary>
    /// Adds each task's agent pick and keeps the architect's difficulty. A pick on disk for a task whose role,
    /// capabilities and prompt are unchanged is kept, so checking the file never asks the model and a rewrite does
    /// not move a pick. Only a write asks the picker, and only for the tasks still without one; a task the plan
    /// assigned a role never gets a pick.
    /// </summary>
    /// <remarks>
    /// A confident answer that the architect's rating does not fit the rubric raises
    /// <see cref="PlanTaskFileRules.DifficultyDisagreed"/>, unless the plan's Decisions table already carries a row
    /// answering that task's rating, and never changes the rating itself. <paramref name="difficultyChecks"/> holds
    /// one line per task for the command's output.
    /// </remarks>
    private PlanTaskFile WithAgents(
        PlanTaskFile file,
        string taskFile,
        PlanTaskFileMode mode,
        string markdown,
        string artifact,
        DiagnosticReport report,
        out IReadOnlyList<string> difficultyChecks)
    {
        difficultyChecks = [];
        if (file.Tasks is not { Count: > 0 } tasks)
            return file;

        Dictionary<string, PlanTaskFileEntry> onDisk = ReadExistingEntries(taskFile);
        List<PlanTaskFileEntry> entries = new(tasks.Count);
        foreach (PlanTaskFileEntry task in tasks)
        {
            PlanTaskFileEntry kept = task;
            if (task.Role is null && task.Id is not null
                && onDisk.TryGetValue(task.Id, out PlanTaskFileEntry? old)
                && old.Agent is not null && old.Role is null
                && ServesConfiguredRoster(old.Agent)
                && string.Equals(old.Capabilities, task.Capabilities, StringComparison.Ordinal)
                && string.Equals(old.Prompt, task.Prompt, StringComparison.Ordinal))
            {
                kept = task with
                {
                    Agent = old.Agent,
                    AgentConfidence = old.AgentConfidence,
                };
            }

            entries.Add(kept);
        }

        Dictionary<string, PlanTaskAgentChoice> picks = new(StringComparer.Ordinal);
        if ((mode is PlanTaskFileMode.Write or PlanTaskFileMode.Convert) && _picker is not null)
        {
            List<PlanTaskFileEntry> open = [.. entries.Where(entry => entry.Role is null && entry.Agent is null)];
            if (open.Count > 0)
            {
                foreach (KeyValuePair<string, PlanTaskAgentChoice> pick in _picker.Pick(open))
                {
                    picks[pick.Key] = pick.Value;
                }

                for (int index = 0; index < entries.Count; index++)
                {
                    PlanTaskFileEntry entry = entries[index];
                    if (entry.Role is null && entry.Agent is null && entry.Id is not null
                        && picks.TryGetValue(entry.Id, out PlanTaskAgentChoice? choice))
                    {
                        entries[index] = entry with
                        {
                            Agent = choice.Agent,
                            AgentConfidence = choice.Confidence,
                        };
                    }
                }
            }
        }

        if (mode is PlanTaskFileMode.Write or PlanTaskFileMode.Convert)
        {
            difficultyChecks = Checks(entries, picks, PlanPacketContext.Parse(markdown), artifact, report);
        }

        // A developer task's rating in use decides its tier, so the file names that tier: a hard task written as
        // the generic developer kind would dispatch dev-medium from the file alone. A pick for a documentation or
        // delivery agent is not a developer tier and stays as it is.
        for (int index = 0; index < entries.Count; index++)
        {
            PlanTaskFileEntry entry = entries[index];
            if (entry.Role is null
                && entry.Technology is not null
                && (entry.Agent is null
                    || string.Equals(entry.Agent, PlanTaskAgentPick.DeveloperKind, StringComparison.Ordinal)))
            {
                entries[index] = entry with { Agent = PlanTaskAgentPick.DeveloperTier(entry.Difficulty, 0) };
            }
        }

        return file with { Tasks = entries };
    }

    /// <summary>
    /// One line per task saying what the decision model made of the architect's rating, and the finding that sends
    /// the plan back to the architect when the model confidently disagreed and no Decisions row already answers it.
    /// </summary>
    private static List<string> Checks(
        IReadOnlyList<PlanTaskFileEntry> entries,
        IReadOnlyDictionary<string, PlanTaskAgentChoice> picks,
        PlanPacketContext plan,
        string artifact,
        DiagnosticReport report)
    {
        List<string> checks = new(entries.Count);
        foreach (PlanTaskFileEntry entry in entries)
        {
            if (entry.Id is not { } id)
                continue;

            DifficultyAgreement? agreement = picks.TryGetValue(id, out PlanTaskAgentChoice? choice)
                ? choice.DifficultyCheck
                : null;
            checks.Add(CheckLine(id, entry.Difficulty, agreement));

            if (agreement is not { IsAgreed: false } || plan.HasDecisionStartingWith($"{id} difficulty"))
                continue;

            report.Add(new Diagnostic(
                PlanTaskFileRules.DifficultyDisagreed,
                Severity.Error,
                $"{id}: the decision model did not agree with the difficulty `{entry.Difficulty}` " +
                $"({PlanTaskAgentPick.TierNameOf(entry.Difficulty)}) by the rubric.",
                id,
                artifact,
                Hint: $"Re-rate the task by the rubric in shared/difficulty-rubric.md, or keep the rating and add a " +
                $"Decisions row '{id} difficulty' that says why."));
        }

        return checks;
    }

    private static string CheckLine(string id, string? difficulty, DifficultyAgreement? agreement) =>
        agreement is null
            ? $"{id} difficulty {difficulty}: unchecked"
            : $"{id} difficulty {difficulty}: " +
              $"{(agreement.IsAgreed ? DifficultyAgreement.Agreed : DifficultyAgreement.NotAgreed)} " +
              $"(confidence {agreement.Confidence.ToString("0.###", CultureInfo.InvariantCulture)})";

    /// <summary>
    /// Whether a recorded pick can still run for the roster this write records. A technology developer under
    /// <c>tiered</c>, or a developer tier under <c>technology</c>, is the previous roster's pick: keeping it would
    /// write an agent the roster does not offer, so the task is picked again.
    /// </summary>
    private bool ServesConfiguredRoster(string agent) =>
        ArbiterSquadCatalog.LoadEmbedded().Agents.TryGetValue(agent, out ArbiterSquadAgent? recorded)
        && (recorded.WorkerRoster is null
            || string.Equals(recorded.WorkerRoster, _config.WorkerRoster, StringComparison.Ordinal));

    private Dictionary<string, PlanTaskFileEntry> ReadExistingEntries(string taskFile)
    {
        Dictionary<string, PlanTaskFileEntry> found = new(StringComparer.Ordinal);
        try
        {
            string path = taskFile;
            string? text = ReadTaskFile(path);
            if (text is null && LegacyTaskFilePath(taskFile) is { } legacy)
            {
                path = legacy;
                text = ReadTaskFile(path);
            }

            if (text is null)
                return found;

            PlanTaskFile? existing = PlanTaskFileFormat.Deserialize(path, text);
            foreach (PlanTaskFileEntry? entry in existing?.Tasks ?? [])
            {
                if (entry?.Id is { } id)
                    found[id] = entry;
            }
        }
        catch (Exception exception) when (FileFailure.Is(exception)
            || exception is JsonException
            || exception is YamlDotNet.Core.YamlException)
        {
            // An unreadable file keeps no picks; the check reports the file itself.
        }

        return found;
    }

    private string? ReadTaskFile(string relativePath)
    {
        string full = Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return _files.ReadTextIfPresent(full);
    }

    /// <summary>The legacy JSON path beside the YAML task file, or null for a name that is not one.</summary>
    private static string? LegacyTaskFilePath(string taskFile)
    {
        if (taskFile.EndsWith(PlanTaskFileRules.LegacyTaskFileSuffix, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PlanTaskFileRules.FileNameOf(taskFile), PlanTaskFileRules.LegacySpecTaskFileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(PlanTaskFileRules.FileNameOf(taskFile), PlanTaskFileRules.SpecTaskFileName, StringComparison.OrdinalIgnoreCase))
        {
            return taskFile[..(taskFile.LastIndexOf('/') + 1)] + PlanTaskFileRules.LegacySpecTaskFileName;
        }

        return taskFile.EndsWith(PlanTaskFileRules.TaskFileSuffix, StringComparison.OrdinalIgnoreCase)
            ? taskFile[..^PlanTaskFileRules.TaskFileSuffix.Length] + PlanTaskFileRules.LegacyTaskFileSuffix
            : null;
    }

    private bool WriteIfChanged(string artifact, string taskFile, string yaml, DiagnosticReport report)
    {
        string full = Path.Combine(_repoRoot, taskFile.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (_files.GetLinkTarget(full) is not null
                || !_files.IsContainedFile(_repoRoot, artifact, _config.Ontology.DocsRoots))
            {
                report.Add(new Diagnostic(
                    PlanTaskFileRules.Malformed,
                    Severity.Error,
                    $"'{taskFile}' is a symbolic link, or its plan is outside the documentation roots, so it is not written.",
                    taskFile,
                    taskFile,
                    Hint: "Remove the link, or pass a plan inside the documentation roots."));
                return false;
            }

            if (string.Equals(_files.ReadTextIfPresent(full), yaml, StringComparison.Ordinal))
            {
                // The YAML is already current, but a JSON file an older release wrote may still sit
                // beside it. Leaving it there would keep two task files, so the write removes it.
                RemoveLegacy(taskFile, report);
                return false;
            }

            _files.WriteAllText(full, yaml);
            RemoveLegacy(taskFile, report);
            return true;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.Malformed,
                Severity.Error,
                $"'{taskFile}' could not be written: {exception.Message}",
                taskFile,
                taskFile));
            return false;
        }
    }

    private void CompareWithDisk(
        string artifact,
        string taskFile,
        PlanTaskFile expected,
        string expectedYaml,
        DiagnosticReport report)
    {
        string fix = TaskFileCommand.Write(artifact);
        string path = taskFile;
        string? diskText;
        try
        {
            diskText = ReadDisk(path);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.Malformed,
                Severity.Error,
                $"The task file '{path}' cannot be read: {exception.Message}",
                path,
                path,
                Hint: $"Run {fix}."));
            return;
        }

        // The JSON form an older release wrote is read in the same place, with one warning that
        // names the convert command. It is compared for plan, digest and task ids only: it does
        // not carry the fields this release writes, so field-by-field differences would call a
        // file that is merely old "wrong".
        bool legacy = false;
        if (diskText is null && LegacyTaskFilePath(taskFile) is { } legacyPath)
        {
            path = legacyPath;
            try
            {
                diskText = ReadDisk(path);
            }
            catch (Exception exception) when (FileFailure.Is(exception))
            {
                report.Add(new Diagnostic(
                    PlanTaskFileRules.Malformed,
                    Severity.Error,
                    $"The task file '{path}' cannot be read: {exception.Message}",
                    path,
                    path,
                    Hint: $"Run {fix}."));
                return;
            }

            if (diskText is not null)
            {
                legacy = true;
                report.Add(new Diagnostic(
                    PlanTaskFileRules.LegacyJsonTaskFile,
                    Severity.Warning,
                    $"The task file '{path}' is in the JSON form an older release wrote.",
                    path,
                    path,
                    Hint: $"Run {TaskFileCommand.Convert(artifact)} to rewrite it as '{taskFile}'."));
            }
        }

        if (diskText is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.MissingTaskFile,
                Severity.Error,
                $"There is no task file at '{taskFile}'.",
                taskFile,
                taskFile,
                Hint: $"Run {fix}."));
            return;
        }

        PlanTaskFile? actual;
        try
        {
            actual = PlanTaskFileFormat.Deserialize(path, diskText);
        }
        catch (Exception exception) when (FileFailure.Is(exception)
            || exception is JsonException
            || exception is YamlDotNet.Core.YamlException)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.Malformed,
                Severity.Error,
                $"The task file '{path}' cannot be read: {exception.Message}",
                path,
                path,
                Hint: $"Run {fix}."));
            return;
        }

        if (actual is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.Malformed,
                Severity.Error,
                $"The task file '{path}' is empty.",
                path,
                path,
                Hint: $"Run {fix}."));
            return;
        }

        int before = report.Items.Count;
        if (actual.SchemaVersion != expected.SchemaVersion
            && actual.SchemaVersion != PlanTaskFileRules.LegacySchemaVersion)
        {
            Differs(report, path, $"schema-version is {actual.SchemaVersion}, the plan produces {expected.SchemaVersion}", fix, PlanTaskFileRules.Malformed);
        }

        if (!string.Equals(actual.Plan, expected.Plan, StringComparison.Ordinal))
        {
            Differs(report, path, $"plan is '{actual.Plan}', expected '{expected.Plan}'", fix);
        }

        if (!string.Equals(actual.ExecutionDigest, expected.ExecutionDigest, StringComparison.OrdinalIgnoreCase))
        {
            Differs(report, path, "execution-digest is stale: the plan moved after the file was written", fix, PlanTaskFileRules.StaleDigest);
        }

        if (legacy)
        {
            HashSet<string> have = [.. (actual.Tasks ?? []).Select(entry => entry.Id ?? string.Empty)];
            foreach (PlanTaskFileEntry want in expected.Tasks ?? [])
            {
                if (!have.Contains(want.Id!))
                {
                    Differs(report, path, $"task '{want.Id}' is missing", fix);
                }
            }

            return;
        }

        CompareTasks(actual, expected, taskFile, fix, report);

        if (report.Items.Count == before && !string.Equals(diskText, expectedYaml, StringComparison.Ordinal))
        {
            Differs(report, taskFile, "the content is the same but the file is not in the form the command writes (formatting)", fix);
        }
    }

    private string? ReadDisk(string relativePath)
    {
        string full = Path.Combine(_repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return _files.ReadTextIfPresent(full);
    }

    /// <summary>
    /// Rewrites a legacy JSON task file as YAML. Nothing is written when there is no JSON file to
    /// convert; the write path removes the JSON once the YAML is in place.
    /// </summary>
    private bool ConvertLegacy(string artifact, string taskFile, string yaml, DiagnosticReport report)
    {
        if (LegacyTaskFilePath(taskFile) is not { } legacy
            || ReadDisk(legacy) is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.LegacyJsonTaskFile,
                Severity.Info,
                $"There is no JSON task file beside '{artifact}', so there is nothing to convert.",
                taskFile,
                taskFile));
            return false;
        }

        bool wrote = WriteIfChanged(artifact, taskFile, yaml, report);
        if (!wrote && ReadDisk(taskFile) is null)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.Malformed,
                Severity.Error,
                $"The JSON task file '{legacy}' exists, but writing '{taskFile}' did not produce it.",
                taskFile,
                taskFile));
        }

        return wrote;
    }

    /// <summary>Removes the JSON file an older release wrote, once its YAML replacement is on disk.</summary>
    private void RemoveLegacy(string taskFile, DiagnosticReport report)
    {
        if (LegacyTaskFilePath(taskFile) is not { } legacy)
        {
            return;
        }

        string full = Path.Combine(_repoRoot, legacy.Replace('/', Path.DirectorySeparatorChar));
        if (!_files.FileExists(full))
        {
            return;
        }

        try
        {
            _files.DeleteFile(full);
            report.Add(new Diagnostic(
                PlanTaskFileRules.LegacyJsonTaskFile,
                Severity.Info,
                $"Removed the JSON task file '{legacy}', now that '{taskFile}' holds the same entries.",
                legacy,
                legacy));
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.Malformed,
                Severity.Warning,
                $"'{legacy}' could not be removed: {exception.Message}",
                legacy,
                legacy,
                Hint: $"Delete it by hand; '{taskFile}' replaces it."));
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
                         ("agent", got.Agent == want.Agent),
                         ("capabilities", got.Capabilities == want.Capabilities),
                         ("session", got.Session == want.Session),
                         ("files", (got.Files ?? []).SequenceEqual(want.Files ?? [])),
                         ("depends-on", (got.DependsOn ?? []).SequenceEqual(want.DependsOn ?? [])),
                         ("difficulty", got.Difficulty == want.Difficulty),
                         ("difficulty-source", got.DifficultySource == want.DifficultySource),
                         ("technology", got.Technology == want.Technology),
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

    private static void Differs(DiagnosticReport report, string taskFile, string what, string fix, string rule = PlanTaskFileRules.DiffersFromPlan) =>
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
