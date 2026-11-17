using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>The rule codes, schema constants and pure path and prompt rules for plan and specification task files.</summary>
public static partial class PlanTaskFileRules
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
    internal const int SniffLength = 65536;

    /// <summary>A copy of a task file left beside it under another name, such as <c>&lt;plan&gt;.tasks.yml.new</c>.</summary>
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

    /// <summary>A task lists a path under the run's scratch folder as one of its files, so no task file is written.</summary>
    public const string ScratchPathInFiles = "KW-DOC-TASKS-013";

    /// <summary>
    /// The next free id in this family: a task file in the JSON form an older release wrote. Warning:
    /// the file is read, and the convert command is named.
    /// </summary>
    public const string LegacyJsonTaskFile = "KW-DOC-TASKS-014";

    /// <summary>The schema version this build reads.</summary>
    internal const int SchemaVersion = 2;

    /// <summary>The schema version an older release wrote, still read with a warning for two minor releases.</summary>
    internal const int LegacySchemaVersion = 1;

    internal const string PlanExtension = ".md";

    /// <summary>The YAML task file beside a plan: <c>&lt;plan&gt;.tasks.yml</c>.</summary>
    internal const string TaskFileSuffix = ".tasks.yml";

    /// <summary>The JSON task file an older release wrote, still read with a warning.</summary>
    internal const string LegacyTaskFileSuffix = ".tasks.json";

    /// <summary>A specification's task list is the file named <c>tasks.md</c>; its task file is <c>tasks.yml</c> beside it.</summary>
    internal const string SpecTaskListFileName = "tasks.md";
    internal const string SpecTaskFileName = "tasks.yml";

    /// <summary>A specification's JSON task file, still read with a warning.</summary>
    internal const string LegacySpecTaskFileName = "tasks.json";

    internal const string PlanKind = "plan";
    internal const string SpecTaskListKind = "specification task list";

    internal static readonly char[] WhitespaceSeparators = [' ', '\t', '\n', '\r'];

    /// <summary>Whether the document is a specification's <c>tasks.md</c>, the task list its <c>tasks.yml</c> is checked against.</summary>
    internal static bool IsSpecTaskList(DocumentModel document) =>
        document.DocType == DocType.Spec
        && string.Equals(FileNameOf(document.RelativePath), SpecTaskListFileName, StringComparison.OrdinalIgnoreCase);

    internal static string KindOf(DocumentModel artifact) =>
        IsSpecTaskList(artifact) ? SpecTaskListKind : PlanKind;

    internal static string FileNameOf(string relativePath) =>
        relativePath[(relativePath.LastIndexOfAny(['/', '\\']) + 1)..];

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
    internal static string? FolderOf(IReadOnlyList<ConfigRegEntry> registry, string property)
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
    /// The task file beside an artifact: <c>&lt;plan&gt;.tasks.yml</c> for a plan, and the folder's
    /// <c>tasks.yml</c> beside a specification's <c>tasks.md</c>.
    /// </summary>
    internal static string TaskFilePath(DocumentModel artifact) =>
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

    /// <summary>
    /// The JSON task file an older release wrote, in the same position as the YAML one. Null when
    /// <paramref name="artifactPath"/> is not a plan or specification task list.
    /// </summary>
    internal static string? LegacyTaskFilePathFor(string artifactPath, bool isSpecTaskList)
    {
        if (isSpecTaskList)
        {
            return artifactPath[..(artifactPath.LastIndexOf('/') + 1)] + LegacySpecTaskFileName;
        }

        return artifactPath.EndsWith(PlanExtension, StringComparison.OrdinalIgnoreCase)
            ? artifactPath[..^PlanExtension.Length] + LegacyTaskFileSuffix
            : artifactPath + LegacyTaskFileSuffix;
    }

    /// <summary>Whether a file name is either form of a task file.</summary>
    internal static bool IsTaskFileName(string fileName) =>
        fileName.EndsWith(TaskFileSuffix, StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(LegacyTaskFileSuffix, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, SpecTaskFileName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, LegacySpecTaskFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path names a specification's task list, by its file name.</summary>
    internal static bool IsSpecTaskListPath(string relativePath) =>
        string.Equals(FileNameOf(relativePath), SpecTaskListFileName, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"```[^\n]*\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FencedBlockRegex();

    [GeneratedRegex(@"`(?<body>[^`\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSpanRegex();

    [GeneratedRegex(
        @"^[ \t]*(?:[-*+][ \t]+)?\**acceptance(?:[ \t]+criteria)?\**\s*:[ \t]*(?<text>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcceptanceLabelRegex();
}
