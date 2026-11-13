using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Processes;

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

    /// <summary>How much of a candidate sidecar is read to see whether it holds the task file schema.</summary>
    private const int SniffLength = 65536;

    /// <summary>A copy of a task file left beside it under another name, such as <c>&lt;plan&gt;.tasks.json.new</c>.</summary>
    public const string SidecarTaskFile = "KW-DOC-TASKS-007";

    /// <summary>A task cites a decision or test contract row the plan does not have, or cites more than its packet can carry. A warning.</summary>
    public const string UnresolvedReference = "KW-DOC-TASKS-008";

    /// <summary>A task points at plan content without saying which ("see above", "as described in the plan"). A warning.</summary>
    public const string VagueReference = "KW-DOC-TASKS-009";

    // KW-DOC-TASKS-010 (a test task ordered before the code its tests name) is retired: a test-first task's one
    // worker writes the tests and the code together, so no task's tests wait on another task. The id is not reused.

    /// <summary>Info: text a task listed as files that is not a repository path was left out of its file scope.</summary>
    public const string NotARepositoryPath = "KW-DOC-TASKS-012";

    /// <summary>A model that read only a task's packet says a developer could not do the task from it. A warning.</summary>
    public const string PacketNotSelfContained = "KW-DOC-TASKS-011";

    /// <summary>The schema version this build reads.</summary>
    private const int SchemaVersion = 1;

    private const string PlanExtension = ".md";
    private const string TaskFileSuffix = ".tasks.json";

    /// <summary>A specification's task list is the file named <c>tasks.md</c>; its task file is <c>tasks.json</c> beside it.</summary>
    private const string SpecTaskListFileName = "tasks.md";
    private const string SpecTaskFileName = "tasks.json";

    private const string PlanKind = "plan";
    private const string SpecTaskListKind = "specification task list";

    private static readonly char[] WhitespaceSeparators = [' ', '\t', '\n', '\r'];

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

    /// <summary>Whether the document is a specification's <c>tasks.md</c>, the task list its <c>tasks.json</c> is checked against.</summary>
    private static bool IsSpecTaskList(DocumentModel document) =>
        document.DocType == DocType.Spec
        && string.Equals(FileNameOf(document.RelativePath), SpecTaskListFileName, StringComparison.OrdinalIgnoreCase);

    private static string KindOf(DocumentModel artifact) =>
        IsSpecTaskList(artifact) ? SpecTaskListKind : PlanKind;

    private static string FileNameOf(string relativePath) =>
        relativePath[(relativePath.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>
    /// Reports each file in the plans folder whose name is a copy of the task file, such as
    /// <c>&lt;plan&gt;.tasks.json.new</c>.
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
        if (Directory.Exists(start))
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
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(directory).EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0 })];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

        foreach (FileSystemInfo entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            string relative = relativeDirectory + "/" + entry.Name;
            if (entry is DirectoryInfo subdirectory)
            {
                if (!InventoryReachability.IsArchived(relative))
                {
                    WalkForSidecars(subdirectory.FullName, relative, report);
                }
            }
            else
            {
                ReportIfSidecar(entry.FullName, relative, report);
            }
        }
    }

    /// <summary>Reports <paramref name="file"/> when it is a copy of a task file.</summary>
    private static void ReportIfSidecar(string file, string relative, DiagnosticReport report)
    {
        // A name that ends in the task file's suffix is the task file itself, or a file
        // KW-DOC-LIFECYCLE-005 already reports; this rule takes only what that one leaves.
        string name = Path.GetFileName(file);
        if (!name.Contains(TaskFileSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(TaskFileSuffix, StringComparison.OrdinalIgnoreCase)
            || !LooksLikeTaskFile(file))
        {
            return;
        }

        report.Add(new Diagnostic(
            SidecarTaskFile,
            Severity.Error,
            $"'{relative}' is a copy of a task file left beside it: the conductor reads only " +
            "'<plan>.tasks.json', so this file is dead weight that the merge would carry.",
            relative,
            relative,
            Hint: "Delete it. If a live plan still needs its task file, run " +
            "'kyber-weave docs tasks <plan> --write', which writes it in place at '<plan>.tasks.json'."));
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
    private static bool LooksLikeTaskFile(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            byte[] head = new byte[SniffLength];
            int read = stream.Read(head, 0, head.Length);
            return HasSchemaKey(head.AsSpan(0, read));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
    /// The repository-relative directories holding plans and specifications: the folders of the
    /// three planning indexes.
    /// </summary>
    /// <remarks>
    /// Read from the registry rather than hard-coded, so a host that moves its plans is checked
    /// against the folder it actually uses. These are the same directories the Arbiter publishes
    /// as <c>config.planning-dirs</c>, which is what lets one detector answer both rules.
    /// </remarks>
    internal static IReadOnlyList<string> PlanningDirectories(IReadOnlyList<ConfigRegEntry> registry)
    {
        string[] properties =
        [
            ConfigRegConfig.PlanIndexProperty,
            ConfigRegConfig.SpecificationIndexProperty,
            ConfigRegConfig.TodoIndexProperty
        ];

        List<string> directories = [];
        foreach (string property in properties)
        {
            if (FolderOf(registry, property) is { } directory)
            {
                directories.Add(directory);
            }
        }

        return directories;
    }

    /// <summary>The folder of a registry property's index file, or null when the property is unset.</summary>
    private static string? FolderOf(IReadOnlyList<ConfigRegEntry> registry, string property)
    {
        string? indexPath = registry
            .FirstOrDefault(entry => string.Equals(entry.Name, property, StringComparison.Ordinal))
            ?.Path;
        if (string.IsNullOrWhiteSpace(indexPath))
        {
            return null;
        }

        string normalized = indexPath.Replace('\\', '/');
        int separator = normalized.LastIndexOf('/');
        return separator > 0 ? normalized[..separator] : null;
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

        if (!DocsRootPath.IsContainedFile(_repoRoot, taskFile, _config.Ontology.DocsRoots))
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

        if (file!.SchemaVersion != SchemaVersion)
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

        CheckTasks(parsed, file, taskFile, artifact.RelativePath, report);
        CheckDigest(parsed, artifact.RelativePath, kind, file, taskFile, report);

        foreach (PlanTaskFileEntry? entry in file.Tasks ?? [])
        {
            if (entry is not null)
            {
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
            string json = File.ReadAllText(FullPath(taskFile));
            file = JsonSerializer.Deserialize<PlanTaskFile>(json, PlanTaskFileJson.ReadOptions);
        }
        catch (JsonException exception)
        {
            report.Add(new Diagnostic(
                Malformed,
                Severity.Error,
                $"Task file '{taskFile}' is not the JSON this rule reads: {exception.Message}",
                taskFile,
                taskFile,
                Hint: $"Do not repair the file by hand. Run {TaskFileCommand.Write(artifactPath)}: it rewrites the whole file from the plan."));
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
                Hint: $"Run {fix}: it adds an entry for '{task.Id}' with its role, files, dependencies and prompt."));
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

        PlanFileScope fileScope = new(_repoRoot);
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
            IReadOnlyList<string> planFiles = fileScope.Filter(task.Files).Kept;
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

    /// <summary>Reports a prompt with no command, no acceptance criteria, or a plan read in it.</summary>
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
    /// True when the prompt runs something: a backticked span or a fenced block holding a program
    /// and at least one argument.
    /// </summary>
    /// <remarks>
    /// The narrow reading is deliberate. A single backticked token is a name — a path, a class, a
    /// setting — and prose that mentions tests does not run them; this repository shipped prompts
    /// whose runner matched no test class, which is the failure a bare mention hides.
    /// </remarks>
    internal static bool HasCommand(string prompt)
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
    /// or <c>Acceptance</c> label, on its line or in the indented block below it.
    /// </summary>
    internal static bool HasAcceptanceCriteria(string prompt)
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

    /// <summary>
    /// The task file beside an artifact: <c>&lt;plan&gt;.tasks.json</c> for a plan, and the folder's
    /// <c>tasks.json</c> beside a specification's <c>tasks.md</c>.
    /// </summary>
    private static string TaskFilePath(DocumentModel artifact) =>
        TaskFilePathFor(artifact.RelativePath, IsSpecTaskList(artifact));

    /// <summary>
    /// The task file's repository-relative path for a plan or specification task list at
    /// <paramref name="artifactPath"/>.
    /// </summary>
    /// <param name="artifactPath">The plan, or the specification's <c>tasks.md</c>, repository-relative.</param>
    /// <param name="isSpecTaskList">True when the path is a specification's task list.</param>
    internal static string TaskFilePathFor(string artifactPath, bool isSpecTaskList)
    {
        if (isSpecTaskList)
        {
            return artifactPath[..(artifactPath.LastIndexOf('/') + 1)] + SpecTaskFileName;
        }

        return artifactPath.EndsWith(PlanExtension, StringComparison.OrdinalIgnoreCase)
            ? artifactPath[..^PlanExtension.Length] + TaskFileSuffix
            : artifactPath + TaskFileSuffix;
    }

    /// <summary>Whether a path names a specification's task list, by its file name.</summary>
    internal static bool IsSpecTaskListPath(string relativePath) =>
        string.Equals(FileNameOf(relativePath), SpecTaskListFileName, StringComparison.OrdinalIgnoreCase);

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
        @"^[ \t]*(?:[-*+][ \t]+)?\**acceptance(?:[ \t]+criteria)?\**\s*:[ \t]*(?<text>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcceptanceLabelRegex();

    // The plan form puts the colon inside the bold, `**Status: Ready**`; the spec form closes the
    // bold after the colon, `**Status:** Ready`. Both name the same state.
    [GeneratedRegex(@"\*\*Status:(?:\*\*)?\s*Ready(?![\w-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReadyStatusRegex();
}
