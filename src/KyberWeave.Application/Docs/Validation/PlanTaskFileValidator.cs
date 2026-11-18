using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Text;
using static KyberWeave.Core.Docs.Validation.PlanTaskFileRules;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks each plan's task file, and each specification's task file, against the task list it was
/// written from, and rules on the prompts it carries.
/// </summary>
/// <remarks>
/// <para>
/// The task file exists so the conductor can dispatch without reading the plan or the specification
/// task list. That source stays the authority, so the file has a shelf life of one source and is
/// valid only while it still describes it: this validator is what stops a conductor pasting a prompt
/// the source has moved past, or a prompt that sends the worker back into the plan the whole
/// arrangement exists to keep out of the conductor's context.
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
/// <para>
/// The sidecar rule, <see cref="SidecarTaskFile" />, walks the plans folder rather than following
/// a plan, so it catches a copy whose plan has already been archived.
/// </para>
/// </remarks>
public sealed partial class PlanTaskFileValidator
{

    private readonly string _repoRoot;
    private readonly IDocsEngineConfig _config;
    private readonly IRepositoryFileStore _files;

    /// <summary>Creates the validator over a repository root and its registry and ontology.</summary>
    /// <param name="repoRoot">The absolute repository root the plan paths are relative to.</param>
    /// <param name="config">The effective host configuration, with its ontology applied.</param>
    /// <param name="files">Reads the task files and walks the plans folder.</param>
    public PlanTaskFileValidator(string repoRoot, IDocsEngineConfig config, IRepositoryFileStore files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(files);

        _repoRoot = repoRoot;
        _config = config;
        _files = files;
    }

    /// <summary>
    /// One finding per disagreement between a plan or specification task list and its task file, and
    /// per bad prompt.
    /// </summary>
    /// <param name="set">The loaded documents; the plans and specification task lists among them are the ones checked.</param>
    public DiagnosticReport Validate(DocumentSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        DiagnosticReport report = new();
        IReadOnlyList<ConfigRegEntry> registry = _config.ConfigReg.Resolve(_config.Ontology);
        IReadOnlyList<string> planningDirectories = PlanningDirectories(registry);

        foreach (DocumentModel artifact in set.Documents.Where(document =>
                     (document.DocType == DocType.Plan || IsSpecTaskList(document))
                     && !InventoryReachability.IsArchived(document.RelativePath)))
        {
            ValidateArtifact(artifact, KindOf(artifact), planningDirectories, report);
        }

        ReportSidecarTaskFiles(registry, report);

        return report;
    }

    /// <summary>
    /// Reports each file in the plans folder whose name is a copy of the task file, such as
    /// <c>&lt;plan&gt;.tasks.yml.new</c>.
    /// </summary>
    /// <remarks>
    /// The walk runs over the folder rather than per plan. A sidecar is picked by its name and confirmed
    /// by its content, so it is reported whether its plan is live, archived, or never existed, which is the state an
    /// abandoned copy is usually in. The file system is read rather than git, so untracked and
    /// ignored files count: a copy that was never committed still reaches the merge's working tree.
    /// </remarks>
    private void ReportSidecarTaskFiles(IReadOnlyList<ConfigRegEntry> registry, DiagnosticReport report)
    {
        string? planFolder = FolderOf(registry, ConfigRegConfig.PlanIndexProperty);
        if (planFolder is null)
        {
            // No folder to scope the walk to: say so, because a clean result here would read as
            // "no sidecars" when nothing was checked.
            report.Add(new Diagnostic(
                SidecarTaskFile,
                Severity.Warning,
                "The plans folder could not be resolved from the 'plan-index' registry property, so " +
                "copies of a task file left beside a plan are not checked.",
                ConfigRegConfig.PlanIndexProperty,
                Hint: "Set 'plan-index' to an index file inside a plans folder, such as 'docs/plans/README.md'."));
            return;
        }

        string start = Path.Combine(_repoRoot, planFolder.Replace('/', Path.DirectorySeparatorChar));
        // A resolved folder that does not exist holds no sidecars, so a clean result is true here;
        // KW-CONFIG-REG-001 already reports a registry index path that points at nothing.
        if (_files.DirectoryExists(start))
        {
            WalkForSidecars(start, planFolder, report);
        }
    }

    /// <summary>
    /// Lists one directory of the plans folder, reports its sidecars and recurses, following no
    /// symbolic link and skipping any <c>archive</c> folder.
    /// </summary>
    /// <remarks>
    /// Each directory is read on its own, so one that cannot be read is reported and the walk goes
    /// on with its siblings: a single locked folder must not hide a sidecar beside it.
    /// </remarks>
    private void WalkForSidecars(string directory, string relativeDirectory, DiagnosticReport report)
    {
        List<RepositoryEntry> entries;
        try
        {
            entries = [.. _files.EnumerateEntries(directory)];
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            // A folder the walk cannot read is a state the run reports: aborting would hide every
            // other finding, and skipping silently would read as "no sidecars".
            report.Add(new Diagnostic(
                SidecarTaskFile,
                Severity.Warning,
                $"'{relativeDirectory}' could not be read ({exception.Message}), so copies of a task file " +
                "left in it are not checked.",
                relativeDirectory,
                Hint: "Fix the folder's permissions or remove it, then run the check again."));
            return;
        }

        foreach (RepositoryEntry entry in entries.OrderBy(e => Path.GetFileName(e.Path), StringComparer.Ordinal))
        {
            if (entry.IsLink)
            {
                continue;
            }

            string relative = relativeDirectory + "/" + Path.GetFileName(entry.Path);
            if (entry.IsDirectory)
            {
                if (!InventoryReachability.IsArchived(relative))
                {
                    WalkForSidecars(entry.Path, relative, report);
                }
            }
            else
            {
                ReportIfSidecar(entry.Path, relative, report);
            }
        }
    }

    /// <summary>Reports <paramref name="file"/> when it is a copy of a task file.</summary>
    private void ReportIfSidecar(string file, string relative, DiagnosticReport report)
    {
        // A name that ends in the task file's suffix is the task file itself, or a file
        // KW-DOC-LIFECYCLE-005 already reports; this rule takes only what that one leaves.
        string name = Path.GetFileName(file);
        if (!name.Contains(".tasks.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(TaskFileSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(LegacyTaskFileSuffix, StringComparison.OrdinalIgnoreCase)
            || !LooksLikeTaskFile(file))
        {
            return;
        }

        report.Add(new Diagnostic(
            SidecarTaskFile,
            Severity.Error,
            $"'{relative}' is a copy of a task file left beside it: the conductor reads only " +
            "'<plan>.tasks.yml', so this file is dead weight that the merge would carry.",
            relative,
            relative,
            Hint: "Delete it. If a live plan still needs its task file, run " +
            "'kyber-weave docs tasks <plan> --write', which writes it in place at '<plan>.tasks.yml'."));
    }

    /// <summary>
    /// True when the file's own top-level keys are the task file schema's, or it cannot be read to
    /// prove otherwise; false when it is gone or holds something else.
    /// </summary>
    /// <remarks>
    /// The name narrows the search and the content decides. The head of the file is read as JSON
    /// and only the keys of its top-level object count, so a note that merely quotes a schema key is
    /// left alone, and a copy cut short by an interrupted write still has the keys it opened with.
    /// A file that cannot be read is reported, because it cannot be shown to be unrelated; one that
    /// has been deleted since the listing is not, because there is nothing left to report.
    /// </remarks>
    private bool LooksLikeTaskFile(string file)
    {
        try
        {
            using Stream stream = _files.OpenRead(file);
            byte[] head = new byte[SniffLength];
            int read = stream.Read(head, 0, head.Length);
            return HasSchemaKey(head.AsSpan(0, read));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            return true;
        }
    }

    /// <summary>
    /// True when the top-level keys are the task file's: its <c>execution-digest</c>, or its
    /// <c>schema-version</c> beside its <c>plan</c> or <c>tasks</c>. A bare <c>schema-version</c>
    /// is a common key in unrelated JSON and proves nothing.
    /// </summary>
    private static bool HasSchemaKey(ReadOnlySpan<byte> head)
    {
        string text = System.Text.Encoding.UTF8.GetString(head);
        int first = 0;
        while (first < text.Length && char.IsWhiteSpace(text[first])) first++;
        if (first >= text.Length || text[first] != '{')
        {
            // YAML: a top-level schema key is enough to prove the shape, because a copy of a task
            // file opens with the keys it was written with.
            bool schema = head.IndexOf("schema-version:"u8) >= 0;
            bool digest = head.IndexOf("execution-digest:"u8) >= 0;
            bool yamlPlanOrTasks = head.IndexOf("\nplan:"u8) >= 0 || head.IndexOf("\n  tasks:"u8) >= 0
                || head.IndexOf("\ntasks:"u8) >= 0;
            return digest || (schema && yamlPlanOrTasks);
        }

        Utf8JsonReader reader = new(head, isFinalBlock: false, state: default);
        bool schemaVersion = false;
        bool planOrTasks = false;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                if (reader.ValueTextEquals("execution-digest"))
                {
                    return true;
                }

                schemaVersion |= reader.ValueTextEquals("schema-version");
                planOrTasks |= reader.ValueTextEquals("plan") || reader.ValueTextEquals("tasks");
            }
        }
        catch (JsonException)
        {
            // Not JSON, or broken before enough keys: judge by the keys read so far.
        }

        return schemaVersion && planOrTasks;
    }

    /// <summary>
    /// Checks one plan or specification task list against the task file beside it. <paramref name="kind"/>
    /// names the artifact in messages: <c>plan</c>, or <c>specification task list</c>.
    /// </summary>
    private void ValidateArtifact(
        DocumentModel artifact,
        string kind,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report)
    {
        PlanDocument parsed = PlanDocumentParser.Parse(artifact.RawMarkdown);
        string taskFile = TaskFilePath(artifact);
        string? legacyTaskFile = LegacyTaskFilePathFor(
            artifact.RelativePath, IsSpecTaskList(artifact));
        bool legacy = false;

        if (!_files.IsContainedFile(_repoRoot, taskFile, _config.Ontology.DocsRoots)
            && legacyTaskFile is not null
            && _files.IsContainedFile(_repoRoot, legacyTaskFile, _config.Ontology.DocsRoots))
        {
            taskFile = legacyTaskFile;
            legacy = true;
        }

        if (!_files.IsContainedFile(_repoRoot, taskFile, _config.Ontology.DocsRoots))
        {
            if (IsReady(artifact))
            {
                report.Add(new Diagnostic(
                    MissingTaskFile,
                    Severity.Warning,
                    $"The {kind} '{artifact.RelativePath}' is Ready and has no task file: the conductor has no prompt " +
                    $"to dispatch from {taskFile}.",
                    artifact.RelativePath,
                    artifact.RelativePath,
                    Hint: $"Run {TaskFileCommand.Write(artifact.RelativePath)}: it writes the task file from the " +
                    $"{kind}, so nothing is written by hand. Or move the status back until it is written."));
            }

            return;
        }

        if (!TryRead(taskFile, artifact.RelativePath, report, out PlanTaskFile? file))
        {
            return;
        }

        // Both forms present is two authorities for one plan. The YAML is the one this build reads,
        // so the JSON copy is a leftover to report with the command that removes it.
        if (!legacy
            && legacyTaskFile is not null
            && _files.FileExists(Path.Combine(_repoRoot, legacyTaskFile.Replace('/', Path.DirectorySeparatorChar))))
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.LegacyJsonTaskFile,
                Severity.Warning,
                $"A JSON task file '{legacyTaskFile}' is left beside '{taskFile}'. This build reads and writes " +
                "the YAML file, so the JSON copy is dead weight that the merge would carry.",
                artifact.RelativePath,
                legacyTaskFile,
                Hint: $"Delete '{legacyTaskFile}', or run {TaskFileCommand.Convert(artifact.RelativePath)} once: " +
                $"it keeps '{taskFile}' and removes the JSON file."));
        }

        if (legacy)
        {
            report.Add(new Diagnostic(
                PlanTaskFileRules.LegacyJsonTaskFile,
                Severity.Warning,
                $"The task file '{taskFile}' is in the JSON form an older release wrote.",
                artifact.RelativePath,
                taskFile,
                Hint: $"Run {TaskFileCommand.Convert(artifact.RelativePath)} to rewrite it as " +
                $"'{TaskFilePath(artifact)}'."));
        }

        if (file!.SchemaVersion == PlanTaskFileRules.LegacySchemaVersion)
        {
            // Version 1 is the older release's schema. It loads; the conversion warning above (or
            // this one for a version-1 YAML file) is the only finding it raises.
            if (!legacy)
            {
                report.Add(new Diagnostic(
                    PlanTaskFileRules.LegacyJsonTaskFile,
                    Severity.Warning,
                    $"The task file '{taskFile}' declares schema version {file.SchemaVersion}; this build writes " +
                    $"version {SchemaVersion}.",
                    artifact.RelativePath,
                    taskFile,
                    Hint: $"Run {TaskFileCommand.Write(artifact.RelativePath)} to rewrite it at the current version."));
            }
        }
        else if (file.SchemaVersion != SchemaVersion)
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' declares schema version {file.SchemaVersion}; this build reads " +
                $"version {SchemaVersion}.",
                artifact.RelativePath,
                taskFile,
                Hint: $"Run {TaskFileCommand.Write(artifact.RelativePath)}: it rewrites the file at the schema " +
                "version this build reads, rather than reading a newer file as if its fields were this one's."));
            return;
        }

        if (string.IsNullOrWhiteSpace(file.Plan))
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' declares no plan, so nothing can tell which {kind} it was written from.",
                artifact.RelativePath,
                taskFile,
                Hint: $"Run {TaskFileCommand.Write(artifact.RelativePath)}: it fills in the plan path."));
            return;
        }

        if (!IsSamePath(file.Plan, artifact.RelativePath))
        {
            report.Add(new Diagnostic(
                DiffersFromPlan,
                Severity.Error,
                $"Task file '{taskFile}' names plan '{file.Plan}' and sits beside '{artifact.RelativePath}'.",
                artifact.RelativePath,
                taskFile,
                Hint: $"A task file belongs to the {kind} it sits beside. Run {TaskFileCommand.Write(artifact.RelativePath)} to rewrite it for this {kind}."));
        }

        CheckWorkerRoster(file, taskFile, artifact.RelativePath, report);
        CheckTasks(parsed, file, taskFile, artifact.RelativePath, report);
        CheckDigest(parsed, artifact.RelativePath, kind, file, taskFile, report);

        // The two forms this build no longer writes keep validating, because they are read with a conversion
        // warning and may predate the Technology field: the legacy JSON file and the version-1 schema. A file that
        // claims the tiered roster is not one of them — it was written under this roster, so its developer tasks
        // owe a technology line even at the old version — and neither is a current file that records the retired
        // roster, which is read as tiered with a warning and needs the line like any other.
        bool claimsTiered = string.Equals(
            file.WorkerRoster?.Trim(), WorkerRosters.Tiered, StringComparison.Ordinal);
        bool predatesTiered = !claimsTiered
            && (legacy || file.SchemaVersion == PlanTaskFileRules.LegacySchemaVersion);

        foreach (PlanTaskFileEntry? entry in file.Tasks ?? [])
        {
            if (entry is not null)
            {
                CheckTaskScope(entry, taskFile, artifact.RelativePath, predatesTiered, report);
                CheckPrompt(entry, taskFile, artifact.RelativePath, planningDirectories, report);
            }
        }
    }

    /// <summary>Reads and parses the file, reporting <see cref="Malformed"/> when either fails.</summary>
    private bool TryRead(string taskFile, string artifactPath, DiagnosticReport report, out PlanTaskFile? file)
    {
        file = null;
        try
        {
            string text = _files.ReadAllTextBounded(FullPath(taskFile));
            file = PlanTaskFileFormat.Deserialize(taskFile, text);
        }
        catch (Exception exception) when (exception is JsonException
            || exception is YamlDotNet.Core.YamlException)
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' is not a JSON or YAML task file this rule reads: {exception.Message}",
                taskFile,
                taskFile,
                Hint: $"Do not repair the file by hand. Run {TaskFileCommand.Write(artifactPath)}: it rewrites the whole file from the plan."));
            return false;
        }
        catch (Exception exception) when (FileFailure.Is(exception) || exception is InvalidDataException)
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' could not be read: {exception.Message}",
                taskFile,
                taskFile,
                Hint: $"Make the file readable, then run {TaskFileCommand.Write(artifactPath)}."));
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
            Hint: $"Run {TaskFileCommand.Write(artifactPath)}: it writes the schema version, the plan path, the digest and one entry per task."));
        return false;
    }

    /// <summary>Reports each task, file scope and dependency on which the file disagrees with the plan.</summary>
    private void CheckTasks(
        PlanDocument plan,
        PlanTaskFile file,
        string taskFile,
        string artifactPath,
        DiagnosticReport report)
    {
        string fix = TaskFileCommand.Write(artifactPath);
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
                    Hint: $"Run {fix}: it writes one entry per plan task, with the plan's id."));
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
                Hint: $"Run {fix}: it adds an entry for '{task.Id}' with its capabilities, files, dependencies and prompt."));
        }

        foreach (string id in declared.Keys.Where(id => !byId.ContainsKey(id)))
        {
            report.Add(new Diagnostic(
                DiffersFromPlan,
                Severity.Error,
                $"Task file '{taskFile}' carries task '{id}', which the plan does not declare.",
                id,
                taskFile,
                Hint: $"The plan is the authority. Run {fix} to drop the entry, or add the task to the plan first and then run it."));
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
                    Hint: $"Run {fix}: it copies the plan's file scope and dependencies."));
            }

            // The task file keeps the repository paths among the plan's file entries, so that is what it is compared to.
            IReadOnlyList<string> planFiles = PlanFileScope.Filter(task.Files).Kept;
            if (!SameSet(planFiles, entry.Files ?? []))
            {
                report.Add(new Diagnostic(
                    DiffersFromPlan,
                    Severity.Error,
                    $"Task '{id}' declares files {Describe(entry.Files ?? [])} in task file '{taskFile}' and " +
                    $"{Describe(planFiles)} in the plan.",
                    id,
                    taskFile,
                    Hint: $"Run {fix}: it copies the plan's file scope. A narrower scope in the file lets a worker be " +
                    "held to a scope it was never given, and a wider one authorizes files the plan did not approve."));
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
                    Hint: $"Run {fix}: it copies the plan's dependencies, so the conductor dispatches in the order " +
                    "the plan's graph declares."));
            }
        }
    }

    /// <summary>Reports a digest that no longer describes the plan or specification task list the file sits beside.</summary>
    private static void CheckDigest(
        PlanDocument parsed,
        string artifactPath,
        string kind,
        PlanTaskFile file,
        string taskFile,
        DiagnosticReport report)
    {
        string expected = PlanDocumentParser.ExecutionDigest(parsed);
        if (string.Equals(file.ExecutionDigest, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The hint names the command that writes the file, never a digest to paste: a pasted value
        // would let someone repair a stale file without regenerating its prompts.
        report.Add(new Diagnostic(
            StaleDigest,
            Severity.Error,
            string.IsNullOrWhiteSpace(file.ExecutionDigest)
                ? $"Task file '{taskFile}' records no execution digest, so nothing shows it was written from this {kind}."
                : $"Task file '{taskFile}' records a stale execution digest: the {kind} moved after the file was written.",
            taskFile,
            taskFile,
            Hint: $"Run {TaskFileCommand.Write(artifactPath)}: it recomputes the digest and rewrites the prompts " +
            $"from the {kind} as it now stands. Never type a digest by hand. The digest covers the tasks, their " +
            "text, files, dependencies and skills, the test contract rows, the out-of-scope paths and the mode, " +
            "so a status or decisions-row edit is not what moved it."));
    }

    /// <summary>
    /// Reports a task file whose recorded roster is not <c>tiered</c>. The file is still read, as <c>tiered</c>,
    /// because there is no other roster to dispatch from; the warning names the value and the change.
    /// </summary>
    /// <remarks>
    /// An unknown value is reported for the same reason as the retired <c>technology</c> one: a value this build
    /// does not know must not select a dispatch path, so it is read as the one roster there is and the author is
    /// told. An absent value is the tiered roster and says nothing.
    /// </remarks>
    private static void CheckWorkerRoster(
        PlanTaskFile file,
        string taskFile,
        string artifactPath,
        DiagnosticReport report)
    {
        string recorded = file.WorkerRoster?.Trim() ?? string.Empty;
        if (recorded.Length == 0 || string.Equals(recorded, WorkerRosters.Tiered, StringComparison.Ordinal))
        {
            return;
        }

        string message = string.Equals(recorded, WorkerRosters.Technology, StringComparison.Ordinal)
            ? $"Task file '{taskFile}' records worker-roster 'technology'. The removal deleted that roster's worker " +
              "agents, so the file is read as 'tiered'."
            : $"Task file '{taskFile}' records worker-roster '{recorded}', which is not a roster. The only roster " +
              "is 'tiered', so the file is read as that.";

        report.Add(new Diagnostic(
            RetiredTechnologyRoster,
            Severity.Warning,
            message,
            artifactPath,
            taskFile,
            Hint: $"Run {TaskFileCommand.Write(artifactPath)}: it rewrites the file under the roster now in use, " +
            "and state a 'Technology:' line on every task a developer agent will do."));
    }

    /// <summary>Reports a prompt with no command, no acceptance criteria, or a plan read in it.</summary>
    /// <summary>
    /// Checks the task's difficulty and technology: the three legal difficulty values, one declared
    /// technology, and no task naming two. A task with neither line reports nothing.
    /// </summary>
    private void CheckTaskScope(
        PlanTaskFileEntry entry,
        string taskFile,
        string artifactPath,
        bool predatesTiered,
        DiagnosticReport report)
    {
        if (!predatesTiered
            && string.IsNullOrWhiteSpace(entry.Technology)
            && NeedsTechnology(entry))
        {
            report.Add(new Diagnostic(
                MissingTechnologyUnderTiered,
                Severity.Error,
                $"Task '{entry.Id}' has no Technology: line, and under the tiered roster a developer task names the one technology it is written in.",
                artifactPath,
                taskFile,
                Hint: "Add 'Technology: <declared technology>'. A documentation or delivery task names its role instead."));
        }

        if (!string.IsNullOrWhiteSpace(entry.Difficulty)
            && !Difficulties.Contains(entry.Difficulty.Trim(), StringComparer.Ordinal))
        {
            report.Add(new Diagnostic(
                InvalidDifficulty,
                Severity.Error,
                $"Task '{entry.Id}' names difficulty '{entry.Difficulty}'. Use one of: easy, medium, hard.",
                artifactPath,
                taskFile));
        }

        if (string.IsNullOrWhiteSpace(entry.Technology))
        {
            return;
        }

        string technology = entry.Technology.Trim();
        if (NamesSeveralTechnologies(technology))
        {
            report.Add(new Diagnostic(
                MultipleTechnologies,
                Severity.Error,
                $"Task '{entry.Id}' names more than one technology ('{technology}'). Split it into one task per technology, with a dependency between them.",
                artifactPath,
                taskFile));
            return;
        }

        IReadOnlyList<string> declared = _config.Ontology.Technologies;
        if (!declared.Contains(technology, StringComparer.Ordinal))
        {
            report.Add(new Diagnostic(
                UndeclaredTechnology,
                Severity.Error,
                $"Task '{entry.Id}' names technology '{technology}', which this repository does not declare. Declared: " +
                (declared.Count == 0 ? "(none)" : string.Join(", ", declared)) + ".",
                artifactPath,
                taskFile,
                Hint: StringDistance.Nearest(technology, declared) is { } nearest
                    ? $"Closest declared technology: '{nearest}'. Declare it in ontology.technologies, or name a declared one."
                    : "Declare it in ontology.technologies, or name a declared one."));
        }
    }

    /// <summary>
    /// Whether a task without a technology belongs to a developer under the tiered roster. A
    /// documentation or delivery task is named by its role or its capabilities; everything else is
    /// developer work the conductor will hand to a developer agent.
    /// </summary>
    /// <remarks>
    /// The worker name a task file records is read as the agent it dispatches to: a removed developer name
    /// resolves to a developer tier, and a removed planning, research or delivery name resolves to its
    /// replacement. Only a task whose worker is a developer tier needs a technology line, because only that
    /// dispatch resolves a technology's rules, skills, standard and gates. A name that resolves to a planner,
    /// a reviewer, `docs-dev`, `devops` or `platform-researcher` needs none.
    /// </remarks>
    private static bool NeedsTechnology(PlanTaskFileEntry entry)
    {
        string role = entry.Role?.Trim() ?? string.Empty;
        string agent = entry.Agent?.Trim() ?? string.Empty;
        string recorded = role.Length > 0 ? role : agent;
        string dispatched = PlanTaskAgentPick.ResolveRemovedAgent(recorded, entry.Difficulty)?.Resolved ?? recorded;
        if (dispatched.Length > 0)
        {
            return PlanTaskAgentPick.DeveloperTiers.Contains(dispatched, StringComparer.Ordinal);
        }

        string capabilities = entry.Capabilities ?? string.Empty;
        return !capabilities.Contains("documentation", StringComparison.OrdinalIgnoreCase)
               && !capabilities.Contains("delivery", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the value names more than one technology: a separator, or the word <c>and</c>. A
    /// technology's own hyphen is not a separator (`data-access-layer` is one name).
    /// </summary>
    private static bool NamesSeveralTechnologies(string value) =>
        value.Contains(',', StringComparison.Ordinal)
        || value.Contains('/', StringComparison.Ordinal)
        || value.Contains('&', StringComparison.Ordinal)
        || value.Contains('+', StringComparison.Ordinal)
        || SeveralTechnologies().IsMatch(value);

    [GeneratedRegex(@"\s+and\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeveralTechnologies();

    private static void CheckPrompt(
        PlanTaskFileEntry entry,
        string taskFile,
        string artifactPath,
        IReadOnlyList<string> planningDirectories,
        DiagnosticReport report)
    {
        string id = string.IsNullOrWhiteSpace(entry.Id) ? taskFile : entry.Id;
        string prompt = entry.Prompt ?? string.Empty;
        string fix = TaskFileCommand.Write(artifactPath);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            report.Add(new Diagnostic(
                IncompletePrompt,
                Severity.Error,
                $"Task '{id}' carries no prompt, so it carries neither a command nor acceptance criteria.",
                id,
                taskFile,
                Hint: $"Run {fix}: it writes the prompt from the plan task. If it reports a gap, add the " +
                "command or the acceptance criteria to that plan task first."));
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
                Hint: $"Add a line to the plan task such as 'Command: `dotnet test --filter FullyQualifiedName~MyTests`' " +
                $"(a program and at least one argument, in backticks), then run {fix}."));
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
                Hint: "Add a line to the plan task such as 'Acceptance criteria: the class reports a non-zero total " +
                $"with no failures.', then run {fix}. Never edit the prompt by hand."));
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
                Hint: $"Remove that instruction from the plan task and put what the worker needs in the task text, " +
                $"then run {fix}. The read guard is advisory where a harness's hooks are, so the prompt is the " +
                "only place the ban can be kept."));
        }
    }

    /// <summary>
    /// True when the plan or specification <c>tasks.md</c> declares itself Ready, which is the state its
    /// task file is written in.
    /// </summary>
    /// <remarks>
    /// Read from the body rather than the frontmatter, because this repository's vocabulary has no Ready
    /// status: an approved plan stays <c>status: current</c> and says <c>**Status: Ready**</c> in its
    /// opening paragraph, and an approved specification's <c>tasks.md</c> says <c>**Status:** Ready</c>
    /// with the colon inside the bold label. <see cref="ReadyStatusRegex"/> accepts both forms.
    /// </remarks>
    private static bool IsReady(DocumentModel artifact) => ReadyStatusRegex().IsMatch(artifact.Body);

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

    // The plan form puts the colon inside the bold, `**Status: Ready**`; the spec form closes the
    // bold after the colon, `**Status:** Ready`. Both name the same state.
    [GeneratedRegex(@"\*\*Status:(?:\*\*)?\s*Ready(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadyStatusRegex();
}
