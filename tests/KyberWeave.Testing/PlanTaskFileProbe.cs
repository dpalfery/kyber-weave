using System.Reflection;
using System.Text.Json;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// One <c>docs validate</c> run over a fixture corpus, captured as the text the command
/// printed so a finding is asserted by the code a conductor reads.
/// </summary>
internal sealed class ReportedFindings
{
    public ReportedFindings(DocFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        DocsValidateSettings settings = new()
        {
            Path = fixture.Root,
            Format = "json"
        };

        if (fixture.Memory is not null)
        {
            Spectre.Console.Testing.TestConsole console = new() { EmitAnsiSequences = false };
            Exit = DocsTestCommands.Validate(DocsMemoryServicesD.Services(fixture), console).Execute(null!, settings);
            Output = console.Output;
            return;
        }

        CapturedConsoleExecution<int> execution =
            ProcessConsoleCapture.Run(() => DocsTestCommands.Validate().Execute(null!, settings));
        Exit = execution.Result;
        Output = execution.Output;
    }

    /// <summary>What the command returned: <c>1</c> when any finding is an error.</summary>
    public int Exit { get; }

    /// <summary>Everything the command printed, both streams joined.</summary>
    public string Output { get; }

    /// <summary>Every <c>KW-*</c> code the run reported, for an assertion message.</summary>
    public string Describe()
    {
        string[] codes =
        [
            .. Output.Split(
                    [' ', '\n', '\r', '"', ',', ':', '[', ']'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.StartsWith("KW-", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        return codes.Length == 0 ? "no KW-* finding" : string.Join(", ", codes);
    }
}

/// <summary>
/// The task-file contract this batch's tests encode: the plan fixture, the task file built
/// from it, and the execution digest the file records.
/// </summary>
/// <remarks>
/// <para>
/// The field names are the contract the validator must read: <c>schema-version</c>,
/// <c>plan</c>, <c>execution-digest</c>, and per task <c>id</c>, <c>role</c>,
/// <c>depends-on</c>, <c>files</c>, <c>session</c> and <c>prompt</c> — kebab-case, to match
/// the rest of this repository's configuration vocabulary.
/// </para>
/// <para>
/// The digest is reached through <see cref="PlanDocumentParser" /> by reflection because this
/// batch is authored before <c>ExecutionDigest(PlanDocument)</c> exists; once it lands the
/// lookup resolves, and what the tests assert is the property — which edits move the digest
/// and which do not — rather than how it is computed.
/// </para>
/// </remarks>
internal static class PlanTaskFileProbe
{
    /// <summary>The schema version this build reads.</summary>
    internal const int SchemaVersion = 2;

    internal const string PlansFolder = "6-Docs/plans";
    internal const string PlanName = "2026-11-07-ready-tasks";
    internal const string PlanPath = PlansFolder + "/" + PlanName + ".md";
    internal const string TaskFilePath = PlansFolder + "/" + PlanName + ".tasks.yml";
    internal const string LegacyTaskFilePath = PlansFolder + "/" + PlanName + ".tasks.json";
    internal const string PlanIndexPath = PlansFolder + "/README.md";

    /// <summary>This fixture's two tasks, the second depending on the first.</summary>
    internal const string FirstTask = "T1";
    internal const string SecondTask = "T2";

    /// <summary>The first task's file scope, as both the plan and the task file declare it.</summary>
    internal static readonly string[] FirstTaskFiles =
    [
        "src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs",
        "src/KyberWeave.Core.Docs/Validation/PlanTaskFile.cs"
    ];

    /// <summary>The second task's file scope.</summary>
    internal static readonly string[] SecondTaskFiles =
        ["tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly YamlDotNet.Serialization.ISerializer Yaml =
        new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.HyphenatedNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(YamlDotNet.Serialization.DefaultValuesHandling.OmitNull)
            .WithIndentedSequences()
            .Build();

    /// <summary>The test class every fixture prompt names. A literal, because this helper lives in
    /// <c>KyberWeave.Testing</c> and cannot see the test assembly's type.</summary>
    internal static string TestClass => "KyberWeave.Tests.PlanTaskFileValidatorTests";

    /// <summary>The direct assembly runner command, fully qualified so it runs its class.</summary>
    internal static string RunnerCommand =>
        "dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class \"" + TestClass + "\"";

    /// <summary>A corpus holding the plan index and the plan, and no task file.</summary>
    internal static DocFixture Corpus(string? planMarkdown = null) => Populate(new DocFixture(), planMarkdown);

    /// <summary>The same corpus on an in-memory file system, for a unit test.</summary>
    internal static DocFixture CorpusInMemory(string? planMarkdown = null) => Populate(DocFixture.CreateInMemory(), planMarkdown);

    private static DocFixture Populate(DocFixture fixture, string? planMarkdown)
    {
        return fixture.WithCatalog()
            .Write(
                ".kyber-weave/kyber-weave.yml",
                """
                ontology:
                  technologies:
                    - csharp
                """)
            .Write(
                PlanIndexPath,
                """
                ---
                id: plans/index
                title: Plans
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-11-07
                ---

                # Plans

                - [Ready tasks](2026-11-07-ready-tasks.md)
                """)
            .Write(PlanPath, planMarkdown ?? PlanMarkdown())
            .Write("src/Placeholder.cs", "// the repository's existing source")
            .Write("tests/Placeholder.cs", "// the repository's existing tests");
    }

    /// <summary>
    /// The plan the task file is written from: two tasks, a dependency between them, a test
    /// contract, a decisions row and an out-of-scope boundary — every field the execution
    /// digest is meant to cover.
    /// </summary>
    internal static string PlanMarkdown() =>
        $$"""
        ---
        id: plans/2026-11-07-ready-tasks
        title: Ready tasks
        doc-type: plan
        status: current
        component: MotorcycleRAG API
        owner: Maintainers
        last-reviewed: 2026-11-07
        development-mode: test-first
        ---

        # Ready tasks

        **Status: Ready.** This plan is approved for execution.

        ## Decisions

        | ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
        |---|---|---|---|---|
        | D1 | Where do the worker prompts live? | In the task file beside the plan. | Tracked in git, so a push carries it. | user: explicit decision 2026-11-07 |

        ## Test contract

        | Task | Test project or file | Runner filter | Observable behaviour | RED evidence required | GREEN acceptance |
        |---|---|---|---|---|---|
        | {{FirstTask}} | `{{SecondTaskFiles[0]}}` | `dotnet test --filter FullyQualifiedName~PlanTaskFileValidatorTests` | The task file is validated. | Fails: the validator is absent. | Passes. |
        | {{SecondTask}} | `{{SecondTaskFiles[0]}}` | `dotnet test --filter FullyQualifiedName~PlanTaskFileValidatorTests` | The file is checked against its plan. | Fails: the drift check is absent. | Passes. |

        ## Tasks

        ### {{FirstTask}}: The task file validator

        - **Files:** {{Backticked(FirstTaskFiles)}}
        - **Depends on:** none
        - **Technology:** csharp
        - **Required skills:** `csharp-dev`

        ### {{SecondTask}}: The drift and prompt rules

        - **Files:** {{Backticked(SecondTaskFiles)}}
        - **Depends on:** {{FirstTask}}
        - **Technology:** csharp
        - **Required skills:** `test-dev`

        ## Out of scope

        - `docs/archive/**`
        """;

    /// <summary>
    /// <see cref="PlanMarkdown" /> with one more task inside its Tasks section, standing for a
    /// plan that moved after its task file was written.
    /// </summary>
    internal static string PlanWithLateTask()
    {
        const string LateTask = """
            ### T3: A task added after the file was written

            - **Files:** `src/KyberWeave.Core.Docs/Validation/Late.cs`
            - **Depends on:** none
            - **Required skills:** `csharp-dev`

            """;

        string plan = PlanMarkdown();
        int outOfScope = plan.IndexOf("## Out of scope", StringComparison.Ordinal);
        Assert.True(outOfScope > 0, "the fixture plan must carry an '## Out of scope' section to insert before.");
        return plan.Insert(outOfScope, LateTask);
    }

    /// <summary>The task file's sidecar: the task file's path with a suffix, as a temporary sibling is named.</summary>
    internal static string SidecarPath(string suffix) => TaskFilePath + suffix;

    /// <summary>An opening paragraph that sends a worker into the plan, which no prompt may do.</summary>
    internal static string PlanReadPrefix() =>
        $"Read `{PlanPath}` before you start, then do the task it describes.\n\n";

    /// <summary>
    /// The task file built from <see cref="PlanMarkdown" />. Each parameter breaks exactly one
    /// thing, so a test names the rule it proves.
    /// </summary>
    /// <param name="planMarkdown">The plan the digest is computed from; defaults to the fixture plan.</param>
    /// <param name="schemaVersion">The declared schema version.</param>
    /// <param name="edit">
    /// One way the file can disagree with the plan: <c>task</c> omits the second task,
    /// <c>files</c> narrows the first task's file scope, and <c>depends-on</c> drops the
    /// dependency the plan declares.
    /// </param>
    /// <param name="missingPromptPart">Removes the prompt's <c>command</c> or its <c>acceptance</c> criteria.</param>
    /// <param name="promptPrefix">Text placed before every prompt.</param>
    /// <param name="digest">The execution digest to record; defaults to the digest of the plan.</param>
    internal static string TaskFile(
        string? planMarkdown = null,
        int schemaVersion = SchemaVersion,
        string? edit = null,
        string? missingPromptPart = null,
        string? promptPrefix = null,
        string? digest = null)
    {
        string plan = planMarkdown ?? PlanMarkdown();
        List<Dictionary<string, object?>> tasks =
        [
            Row(
                FirstTask,
                "csharp-dev",
                edit == "files" ? [FirstTaskFiles[0]] : FirstTaskFiles,
                [],
                Prompt(FirstTask, missingPromptPart, promptPrefix)),
            Row(
                SecondTask,
                "test-dev",
                SecondTaskFiles,
                edit == "depends-on" ? [] : [FirstTask],
                Prompt(SecondTask, missingPromptPart, promptPrefix))
        ];

        if (edit == "task")
        {
            tasks.RemoveAt(1);
        }

        Dictionary<string, object?> file = new(StringComparer.Ordinal)
        {
            ["schema-version"] = schemaVersion,
            ["plan"] = PlanPath,
            ["execution-digest"] = digest ?? ExecutionDigest(plan),
            ["tasks"] = tasks
        };

        return Yaml.Serialize(file);
    }

    /// <summary>The same task file in the JSON form an older release wrote, for the conversion tests.</summary>
    internal static string LegacyJsonTaskFile(
        string? planMarkdown = null,
        int schemaVersion = 1,
        string? edit = null,
        string? missingPromptPart = null,
        string? promptPrefix = null,
        string? digest = null)
    {
        string plan = planMarkdown ?? PlanMarkdown();
        List<Dictionary<string, object?>> tasks =
        [
            Row(
                FirstTask,
                "csharp-dev",
                edit == "files" ? [FirstTaskFiles[0]] : FirstTaskFiles,
                [],
                Prompt(FirstTask, missingPromptPart, promptPrefix)),
            Row(
                SecondTask,
                "test-dev",
                SecondTaskFiles,
                edit == "depends-on" ? [] : [FirstTask],
                Prompt(SecondTask, missingPromptPart, promptPrefix))
        ];

        if (edit == "task")
        {
            tasks.RemoveAt(1);
        }

        Dictionary<string, object?> file = new(StringComparer.Ordinal)
        {
            ["schema-version"] = schemaVersion,
            ["plan"] = PlanPath,
            ["execution-digest"] = digest ?? ExecutionDigest(plan),
            ["tasks"] = tasks
        };

        return JsonSerializer.Serialize(file, JsonOptions);
    }

    /// <summary>
    /// The execution digest of the plan as parsed — the value a task file records. This is the
    /// method D18 specifies; the assertion states its absence rather than failing on a null
    /// reference.
    /// </summary>
    internal static string ExecutionDigest(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        MethodInfo? executionDigest = typeof(PlanDocumentParser).GetMethod(
            "ExecutionDigest",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(PlanDocument)]);
        Assert.True(
            executionDigest is not null,
            "PlanDocumentParser.ExecutionDigest(PlanDocument) is absent, so this batch cannot build a task " +
            "file whose digest matches its plan — and no file can be built at all without the task-file " +
            "model it belongs to (D18).");

        return (string)executionDigest!.Invoke(null, [PlanDocumentParser.Parse(markdown)])!;
    }

    /// <summary>The fully qualified class names every prompt's runner command asks for.</summary>
    internal static IEnumerable<string> PromptClassNames()
    {
        const string ClassOption = "-class \"";
        string[] prompts = [Prompt(FirstTask, null, null), Prompt(SecondTask, null, null)];
        foreach (string prompt in prompts)
        {
            int marker = prompt.IndexOf(ClassOption, StringComparison.Ordinal);
            Assert.True(marker >= 0, "every fixture prompt must carry the direct assembly runner with -class.");

            int start = marker + ClassOption.Length;
            int end = prompt.IndexOf('"', start);
            Assert.True(end > start, "the fixture prompt's -class argument is not closed.");

            yield return prompt[start..end];
        }
    }

    private static string Backticked(IEnumerable<string> paths) =>
        string.Join(", ", paths.Select(path => "`" + path + "`"));

    private static Dictionary<string, object?> Row(
        string id,
        string role,
        IReadOnlyList<string> files,
        IReadOnlyList<string> dependsOn,
        string prompt) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["role"] = role,
            ["depends-on"] = dependsOn,
            ["files"] = files,
            ["session"] = id,
            ["prompt"] = prompt,
            ["technology"] = "csharp"
        };

    private static string Prompt(string taskId, string? missingPromptPart, string? promptPrefix)
    {
        string command = missingPromptPart == "command"
            ? "Run the tests this task owns and report the total."
            : $"Run `{RunnerCommand}` and report the total it prints.";

        string acceptance = missingPromptPart == "acceptance"
            ? "Make the change read correctly."
            : "Acceptance criteria: the class named in the command reports a non-zero total, with no failures.";

        string body =
            $"""
             Implement task {taskId} in the files this task names.

             {command}

             {acceptance}
             """;

        return (promptPrefix ?? string.Empty) + body + "\n";
    }
}

/// <summary>
/// The specification task-file contract: a spec folder holding <c>tasks.md</c>, the file a
/// specification's task list is written in, and the <c>tasks.json</c> beside it that the
/// validator must read. The spec's requirements and design are present so the rules can be shown
/// to ignore them.
/// </summary>
/// <remarks>
/// The author's files and dependencies are hard-coded here rather than read back from the parser,
/// so a task file never inherits a parse defect: the digest is the only value computed from the
/// parser, and the tests that need it compute it from the same parser the validator uses.
/// </remarks>
internal static class SpecTaskFileProbe
{
    internal const string SpecFolder = "6-Docs/specs/ready-feature";
    internal const string SpecIndexPath = "6-Docs/specs/README.md";
    internal const string TasksPath = SpecFolder + "/tasks.md";
    internal const string TaskFilePath = SpecFolder + "/tasks.yml";
    internal const string LegacyTaskFilePath = SpecFolder + "/tasks.json";
    internal const string DesignPath = SpecFolder + "/design.md";

    /// <summary>This fixture's two spec tasks, the second depending on the first.</summary>
    internal const string FirstTask = "1.1";
    internal const string SecondTask = "1.2";

    /// <summary>Each task's file scope, as both the task list and the task file declare it.</summary>
    internal const string FirstTaskFile = "tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs";
    internal const string SecondTaskFile = "src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs";

    private static readonly YamlDotNet.Serialization.ISerializer Yaml =
        new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.HyphenatedNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(YamlDotNet.Serialization.DefaultValuesHandling.OmitNull)
            .WithIndentedSequences()
            .Build();

    /// <summary>A corpus holding the specification index, the spec folder and its four files.</summary>
    internal static DocFixture Corpus(string? tasksMarkdown = null) => Populate(new DocFixture(), tasksMarkdown);

    /// <summary>The same corpus on an in-memory file system, for a unit test.</summary>
    internal static DocFixture CorpusInMemory(string? tasksMarkdown = null) => Populate(DocFixture.CreateInMemory(), tasksMarkdown);

    private static DocFixture Populate(DocFixture fixture, string? tasksMarkdown)
    {
        return fixture.WithCatalog()
            .Write(
                ".kyber-weave/kyber-weave.yml",
                """
                ontology:
                  technologies:
                    - csharp
                """)
            .Write(
                SpecIndexPath,
                """
                ---
                id: specs/index
                title: Specifications
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-11-12
                ---

                # Specifications

                - [Ready feature](ready-feature/README.md)
                """)
            .Write(
                SpecFolder + "/README.md",
                """
                ---
                id: specs/ready-feature/index
                title: Ready feature
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-11-12
                ---

                # Ready feature

                - [Requirements](requirements.md)
                - [Design](design.md)
                - [Tasks](tasks.md)
                """)
            .Write(SpecFolder + "/requirements.md", SpecDocument("requirements", "**Status:** Ready. The requirements are approved."))
            .Write(DesignPath, SpecDocument("design", "**Status:** Ready. The design is approved."))
            .Write(TasksPath, tasksMarkdown ?? TasksMarkdown())
            .Write("src/Placeholder.cs", "// the repository's existing source")
            .Write("tests/Placeholder.cs", "// the repository's existing tests");
    }

    /// <summary>
    /// The specification task list: frontmatter, the spec Ready line, a Phase status label, and two
    /// checkbox tasks in the spec grammar, each with its objective, files, acceptance criteria,
    /// skills, dependencies and requirement references.
    /// </summary>
    internal static string TasksMarkdown() =>
        """
        ---
        id: specs/ready-feature/tasks
        title: Ready feature tasks
        doc-type: spec
        status: current
        component: MotorcycleRAG API
        owner: Maintainers
        last-reviewed: 2026-11-12
        development-mode: test-first
        ---

        # Ready feature tasks

        **Status:** Ready. This specification is approved for execution.

        **Phase status:** Approved

        ## Test contract

        | Task | Test project or file | Runner filter | Observable behaviour | RED evidence required | GREEN acceptance |
        |---|---|---|---|---|---|
        | 1.1 | `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs` | `dotnet test --filter FullyQualifiedName~PlanTaskFileValidatorTests` | The task file is validated. | Fails: the validator is absent. | Passes. |
        | 1.2 | `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs` | `dotnet test --filter FullyQualifiedName~PlanTaskFileValidatorTests` | The file is checked against its task list. | Fails: the drift check is absent. | Passes. |

        ## Tasks

        ### Phase 1

        - [ ] 1.1 RED: Pin the specification task file rules
          - **Objective:** Pin the rules a specification task file is checked against.
          - **Files:**
            - `tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs`
          - **Acceptance:**
            - `Widget.Flag` is reported when the digest is stale.
          - **Technology:** csharp
          - **Skills:** `test-dev`
          - **Depends on:** none
          - _Requirements: 1.1, 1.2_
        - [ ] 1.2 GREEN: Check the specification task file against its task list
          - **Objective:** Make the validator read the specification task file.
          - **Files:**
            - `src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs`
          - **Acceptance:**
            - `Widget.Flag` is reported when a task is missing.
          - **Technology:** csharp
          - **Skills:** `csharp-dev`
          - **Depends on:** 1.1
          - _Requirements: 1.1, 1.2_
        """;

    /// <summary>
    /// The prompt for one spec task: it names the runner command with its class and the acceptance
    /// criteria line, as the plan fixture's prompts do, and it sends the worker to no planning file.
    /// </summary>
    internal static string Prompt(string taskId) =>
        $"Implement task {taskId} in the files this task names.\n\n" +
        $"Run `{PlanTaskFileProbe.RunnerCommand}` and report the total it prints.\n\n" +
        "Acceptance criteria: the class named in the command reports a non-zero total, with no failures.\n";

    /// <summary>
    /// The task file built from the spec's task list. <paramref name="digest"/> defaults to the
    /// digest the parser computes for <paramref name="tasksMarkdown"/>, and <paramref name="planField"/>
    /// defaults to <see cref="TasksPath"/>.
    /// </summary>
    /// <param name="tasksMarkdown">The task list the digest is computed from; defaults to the fixture task list.</param>
    /// <param name="digest">The execution digest to record.</param>
    /// <param name="planField">The <c>plan</c> field to record; defaults to the task list's path.</param>
    /// <param name="edit">A change to the task entries before the file is written, to break one thing.</param>
    internal static string TaskFile(
        string? tasksMarkdown = null,
        string? digest = null,
        string? planField = null,
        Action<List<Dictionary<string, object?>>>? edit = null)
    {
        List<Dictionary<string, object?>> tasks =
        [
            Row(FirstTask, "test-dev", [FirstTaskFile], [], Prompt(FirstTask)),
            Row(SecondTask, "csharp-dev", [SecondTaskFile], [FirstTask], Prompt(SecondTask))
        ];

        edit?.Invoke(tasks);

        Dictionary<string, object?> file = new(StringComparer.Ordinal)
        {
            ["schema-version"] = PlanTaskFileProbe.SchemaVersion,
            ["plan"] = planField ?? TasksPath,
            ["execution-digest"] = digest
                ?? PlanDocumentParser.ExecutionDigest(PlanDocumentParser.Parse(tasksMarkdown ?? TasksMarkdown())),
            ["tasks"] = tasks
        };

        return Yaml.Serialize(file);
    }

    /// <summary>
    /// The digest the Arbiter's plan command prints for the spec's <c>tasks.md</c>, read from the
    /// command's own JSON output, so the task file records the value a conductor would copy.
    /// </summary>
    internal static string ReportedDigest(DocFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (fixture.Memory is not null)
        {
            // The digest the plan service reports, which the plan command prints, read over the in-memory checkout.
            return new KyberWeave.Application.Arbiter.ArbiterPlanService(fixture.Memory, fixture.Paths)
                .TryRead(fixture.Root, TasksPath, TasksPath, new DiagnosticReport())!.Digest!;
        }

        string path = Path.Combine(fixture.Root, TasksPath.Replace('/', Path.DirectorySeparatorChar));
        ArbiterPlanCommand command = ArbiterTestServices.Command<ArbiterPlanCommand>();
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => command.Execute(null!, new ArbiterSettings { Path = path, Format = "json" }));

        return ExecutionDigestFrom(execution.Output);
    }

    /// <summary>
    /// Reads <c>metrics.execution-digest</c> from the plan command's JSON. Each shape check fails the
    /// test with the output named, rather than throwing from the reader.
    /// </summary>
    private static string ExecutionDigestFrom(string output)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException exception)
        {
            Assert.Fail($"'arbiter plan -f json' did not print one JSON document ({exception.Message}); output: {output}");
            throw;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("metrics", out JsonElement metrics)
                || metrics.ValueKind != JsonValueKind.Object)
            {
                Assert.Fail($"'arbiter plan -f json' printed no 'metrics' object; output: {output}");
                return string.Empty;
            }

            if (!metrics.TryGetProperty("execution-digest", out JsonElement digest)
                || digest.ValueKind != JsonValueKind.String)
            {
                Assert.Fail($"'arbiter plan -f json' printed no string 'metrics.execution-digest'; output: {output}");
                return string.Empty;
            }

            return digest.GetString() ?? string.Empty;
        }
    }

    private static string SpecDocument(string name, string body) =>
        $"""
        ---
        id: specs/ready-feature/{name}
        title: Ready feature {name}
        doc-type: spec
        status: current
        component: MotorcycleRAG API
        owner: Maintainers
        last-reviewed: 2026-11-12
        ---

        # Ready feature {name}

        {body}
        """;

    private static Dictionary<string, object?> Row(
        string id,
        string role,
        IReadOnlyList<string> files,
        IReadOnlyList<string> dependsOn,
        string prompt) =>
        new(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["role"] = role,
            ["depends-on"] = dependsOn,
            ["files"] = files,
            ["session"] = id,
            ["prompt"] = prompt,
            ["technology"] = "csharp"
        };
}
