using System.Reflection;
using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Arbiter.Plans;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The per-plan task file: <c>&lt;plan&gt;.tasks.json</c> beside the plan, holding each
/// worker's prompt. The conductor has the docs tools and no shell, so every rule here is
/// reached through <c>docs validate</c> — the surface the orchestrator actually holds —
/// rather than by calling the validator directly.
/// </summary>
/// <remarks>
/// <para>
/// The file is duplicated from the plan on purpose, and the duplication is what these rules
/// check: a task file that has drifted from its plan hands a worker work the plan does not
/// describe. The prompts are pasted unchanged at dispatch, so a prompt with no command, or
/// one that sends a worker back into the plan, is a defect nothing else catches.
/// </para>
/// <para>
/// <c>KW-DOC-TASKS-006</c> is the one warning: a plan marked Ready with no task file beside it
/// is the state every plan is in until the architect writes one, so it must advise without
/// failing a merge.
/// </para>
/// <para>
/// <c>KW-DOC-TASKS-007</c> is the one error about a file beside the task file: a temporary
/// sibling such as <c>&lt;plan&gt;.tasks.json.new</c> left in the plans folder is a copy of the
/// prompts that nothing else reads, so it fails the run whether or not its plan is still there.
/// </para>
/// <para>
/// Each rule's test carries its own control — the same corpus with the file as authored
/// reports nothing — so no finding below can come from the fixture instead of the edit.
/// </para>
/// </remarks>
public sealed class PlanTaskFileValidatorTests
{
    /// <summary>The file is malformed, or declares a schema version this build does not read.</summary>
    private const string Malformed = "KW-DOC-TASKS-001";

    /// <summary>The file's tasks, files or dependencies disagree with the plan's.</summary>
    private const string DiffersFromPlan = "KW-DOC-TASKS-002";

    /// <summary>The file's execution digest is stale, so the plan moved after the file was written.</summary>
    private const string StaleDigest = "KW-DOC-TASKS-003";

    /// <summary>A prompt carries no command, or no acceptance criteria.</summary>
    private const string IncompletePrompt = "KW-DOC-TASKS-004";

    /// <summary>A prompt tells the worker to open a plan, which the read guard cannot stop.</summary>
    private const string PlanReadInstruction = "KW-DOC-TASKS-005";

    /// <summary>A Ready plan has no task file beside it. Advisory: it never fails a merge.</summary>
    private const string MissingTaskFile = "KW-DOC-TASKS-006";

    /// <summary>A file in the plans folder is a copy of a task file under another name, such as a temporary sibling.</summary>
    private const string SidecarTaskFile = "KW-DOC-TASKS-007";

    private static ReportedFindings Validate(DocFixture fixture) => new(fixture);

    /// <summary>Asserts the code was reported, naming the codes the run did report.</summary>
    private static void AssertReports(ReportedFindings run, string rule)
    {
        Assert.True(
            run.Output.Contains(rule, StringComparison.Ordinal),
            $"expected {rule} from 'docs validate'; it reported {run.Describe()}.");
    }

    /// <summary>
    /// Asserts the code was not reported. Without this half a validator that fires on every
    /// file would satisfy each rule's own test.
    /// </summary>
    private static void AssertDoesNotReport(ReportedFindings run, string rule)
    {
        Assert.False(
            run.Output.Contains(rule, StringComparison.Ordinal),
            $"did not expect {rule} from 'docs validate'; it reported {run.Describe()}.");
    }

    /// <summary>A truncated file cannot be read at all, which is the malformed case.</summary>
    [Fact]
    public void ATruncatedTaskFileIsKWDOCTASKS001()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(PlanTaskFileProbe.TaskFilePath, "{\n  \"schema-version\": 1,\n  \"tasks\": [");

        ReportedFindings run = Validate(fixture);

        AssertReports(run, Malformed);
        Assert.Equal(1, run.Exit);
    }

    /// <summary>
    /// A schema version this build does not read is malformed rather than silently valid: the
    /// version decides which fields exist, so a future file read as this one would lose prompts
    /// without saying so.
    /// </summary>
    [Fact]
    public void AnUnreadSchemaVersionIsKWDOCTASKS001()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile(schemaVersion: 99));

        AssertReports(Validate(fixture), Malformed);
    }

    /// <summary>
    /// The whole reason the file is checked: the plan is the authority, so a task the file
    /// invents, a file scope it narrows, and a dependency it drops are the same defect.
    /// </summary>
    /// <param name="edit">
    /// <c>task</c> omits a plan task, <c>files</c> narrows one task's file scope, and
    /// <c>depends-on</c> drops a dependency the plan declares.
    /// </param>
    [Theory]
    [InlineData("task")]
    [InlineData("files")]
    [InlineData("depends-on")]
    public void ADriftFromThePlanIsKWDOCTASKS002(string edit)
    {
        using DocFixture accepted = PlanTaskFileProbe.Corpus();
        accepted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), DiffersFromPlan);

        using DocFixture drifted = PlanTaskFileProbe.Corpus();
        drifted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile(edit: edit));

        AssertReports(Validate(drifted), DiffersFromPlan);
    }

    /// <summary>
    /// A null task entry inside the tasks list reports KW-DOC-TASKS-002 instead of crashing
    /// with a NullReferenceException.
    /// </summary>
    [Fact]
    public void ANullTaskEntryIsKWDOCTASKS002()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string taskFile = $$"""
            {
              "schema-version": 1,
              "plan": "{{PlanTaskFileProbe.PlanPath}}",
              "execution-digest": "{{PlanTaskFileProbe.ExecutionDigest(PlanTaskFileProbe.PlanMarkdown())}}",
              "tasks": [null]
            }
            """;
        fixture.Write(PlanTaskFileProbe.TaskFilePath, taskFile);

        ReportedFindings run = Validate(fixture);
        AssertReports(run, DiffersFromPlan);
    }

    /// <summary>
    /// A null element inside files or depends-on does not crash Normalize with a
    /// NullReferenceException and reports KW-DOC-TASKS-002.
    /// </summary>
    [Fact]
    public void ANullFileOrDependencyElementIsKWDOCTASKS002()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string taskFile = $$"""
            {
              "schema-version": 1,
              "plan": "{{PlanTaskFileProbe.PlanPath}}",
              "execution-digest": "{{PlanTaskFileProbe.ExecutionDigest(PlanTaskFileProbe.PlanMarkdown())}}",
              "tasks": [
                {
                  "id": "{{PlanTaskFileProbe.FirstTask}}",
                  "role": "csharp-dev",
                  "depends-on": [null],
                  "files": [null],
                  "session": "{{PlanTaskFileProbe.FirstTask}}",
                  "prompt": "run `dotnet test`\n\nAcceptance criteria:\n- passes"
                }
              ]
            }
            """;
        fixture.Write(PlanTaskFileProbe.TaskFilePath, taskFile);

        ReportedFindings run = Validate(fixture);
        AssertReports(run, DiffersFromPlan);
    }

    /// <summary>
    /// A blank element inside depends-on reports KW-DOC-TASKS-002 even when the plan task
    /// declares no dependencies: normalization must not make <c>[null]</c> compare equal to
    /// <c>[]</c>, or junk passes as a matching scope.
    /// </summary>
    [Fact]
    public void ABlankDependencyElementAgainstAnEmptyPlanScopeIsKWDOCTASKS002()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string taskFile = $$"""
            {
              "schema-version": 1,
              "plan": "{{PlanTaskFileProbe.PlanPath}}",
              "execution-digest": "{{PlanTaskFileProbe.ExecutionDigest(PlanTaskFileProbe.PlanMarkdown())}}",
              "tasks": [
                {
                  "id": "{{PlanTaskFileProbe.FirstTask}}",
                  "role": "csharp-dev",
                  "depends-on": [null],
                  "files": ["src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs", "src/KyberWeave.Core.Docs/Validation/PlanTaskFile.cs"],
                  "session": "{{PlanTaskFileProbe.FirstTask}}",
                  "prompt": "run `dotnet test`\n\nAcceptance criteria:\n- passes"
                },
                {
                  "id": "{{PlanTaskFileProbe.SecondTask}}",
                  "role": "test-dev",
                  "depends-on": ["{{PlanTaskFileProbe.FirstTask}}"],
                  "files": ["tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs"],
                  "session": "{{PlanTaskFileProbe.SecondTask}}",
                  "prompt": "run `dotnet test`\n\nAcceptance criteria:\n- passes"
                }
              ]
            }
            """;
        fixture.Write(PlanTaskFileProbe.TaskFilePath, taskFile);

        ReportedFindings run = Validate(fixture);
        AssertReports(run, DiffersFromPlan);
    }

    /// <summary>
    /// A digest that matches the plan means the file was written from this plan. A stale one
    /// means the plan moved afterwards, so the conductor refuses the file until the architect
    /// regenerates it rather than pasting prompts the plan no longer describes.
    /// </summary>
    [Fact]
    public void AStaleDigestIsKWDOCTASKS003()
    {
        using DocFixture accepted = PlanTaskFileProbe.Corpus();
        accepted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), StaleDigest);

        // The plan gains a task after the file was written, so the recorded digest no longer
        // describes the plan it was written from.
        using DocFixture stale = PlanTaskFileProbe.Corpus(PlanTaskFileProbe.PlanWithLateTask());
        stale.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());

        AssertReports(Validate(stale), StaleDigest);
    }

    /// <summary>
    /// A prompt is pasted unchanged, so anything it omits is omitted from the worker's whole
    /// brief. A command is how the worker verifies its change; acceptance criteria are how it
    /// knows the change is done.
    /// </summary>
    /// <remarks>
    /// The detection the design leaves unstated is pinned here: a command is a backticked or
    /// fenced argv that runs, and acceptance criteria are the text under an
    /// <c>Acceptance criteria</c> label. Both are the narrow readings — prose that mentions
    /// testing is not a command, and a prompt that leaves the criteria out is reported.
    /// </remarks>
    /// <param name="missingPromptPart">Removes the prompt's <c>command</c> or its <c>acceptance</c> criteria.</param>
    [Theory]
    [InlineData("command")]
    [InlineData("acceptance")]
    public void APromptWithoutACommandOrAcceptanceCriteriaIsKWDOCTASKS004(string missingPromptPart)
    {
        using DocFixture accepted = PlanTaskFileProbe.Corpus();
        accepted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), IncompletePrompt);

        using DocFixture incomplete = PlanTaskFileProbe.Corpus();
        incomplete.Write(
            PlanTaskFileProbe.TaskFilePath,
            PlanTaskFileProbe.TaskFile(missingPromptPart: missingPromptPart));

        AssertReports(Validate(incomplete), IncompletePrompt);
    }

    /// <summary>
    /// The read guard is advisory where hooks are, so a prompt that tells a worker to open the
    /// plan is the one place the ban can still be enforced. It shares its detection with
    /// <c>KW-ARB-PLANREAD-001</c>: the same instruction, caught at validation on the harnesses
    /// whose dispatch hook could not fire.
    /// </summary>
    [Fact]
    public void APromptThatTellsAWorkerToOpenThePlanIsKWDOCTASKS005()
    {
        using DocFixture accepted = PlanTaskFileProbe.Corpus();
        accepted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), PlanReadInstruction);

        using DocFixture readsThePlan = PlanTaskFileProbe.Corpus();
        readsThePlan.Write(
            PlanTaskFileProbe.TaskFilePath,
            PlanTaskFileProbe.TaskFile(promptPrefix: PlanTaskFileProbe.PlanReadPrefix()));

        AssertReports(Validate(readsThePlan), PlanReadInstruction);
    }

    /// <summary>
    /// The advisory case: every plan is in this state until its architect writes the file, so
    /// it must be reported and must not fail the merge that carries an unconverted plan.
    /// </summary>
    [Fact]
    public void AReadyPlanWithoutATaskFileWarnsWithoutFailing()
    {
        using DocFixture accepted = PlanTaskFileProbe.Corpus();
        accepted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), MissingTaskFile);

        using DocFixture missing = PlanTaskFileProbe.Corpus();

        ReportedFindings run = Validate(missing);

        AssertReports(run, MissingTaskFile);
        Assert.Equal(0, run.Exit);
    }

    /// <summary>
    /// Issue 599: an architect wrote the task file through a temporary sibling such as
    /// <c>&lt;plan&gt;.tasks.json.new</c> and left it behind, and no rule read it. The copy holds
    /// the same prompts, so it is a second, unchecked task file that a later write could pick up
    /// in place of the one the plan names.
    /// </summary>
    /// <param name="suffix">The text appended to the task file's name to make the sidecar.</param>
    [Theory]
    [InlineData(".new")]
    [InlineData(".tmp")]
    [InlineData(".bak")]
    [InlineData("~")]
    public void ASidecarCopyOfATaskFileIsKWDOCTASKS007(string suffix)
    {
        using DocFixture accepted = PlanTaskFileProbe.Corpus();
        accepted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), SidecarTaskFile);

        using DocFixture sidecar = PlanTaskFileProbe.Corpus();
        sidecar.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        sidecar.Write(PlanTaskFileProbe.SidecarPath(suffix), PlanTaskFileProbe.TaskFile());

        ReportedFindings run = Validate(sidecar);

        AssertReports(run, SidecarTaskFile);
        Assert.Equal(1, run.Exit);
        Assert.Contains(PlanTaskFileProbe.SidecarPath(suffix), run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue 599's leftover copy can outlive the plan it was written from, as when an archived
    /// plan's sidecar is still in the folder. The copy is reported whether or not a plan sits
    /// beside it, so the rule matches on the file's name alone.
    /// </summary>
    [Fact]
    public void ASidecarWhosePlanIsGoneIsStillKWDOCTASKS007()
    {
        const string OrphanSidecar = "6-Docs/plans/2026-11-05-archived-plan.tasks.json.new";

        using DocFixture accepted = PlanTaskFileProbe.Corpus();
        accepted.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), SidecarTaskFile);

        using DocFixture orphan = PlanTaskFileProbe.Corpus();
        orphan.Write(OrphanSidecar, PlanTaskFileProbe.TaskFile());

        ReportedFindings run = Validate(orphan);

        AssertReports(run, SidecarTaskFile);
        Assert.Equal(1, run.Exit);
        Assert.Contains(OrphanSidecar, run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule is keyed to the sidecar's name and nothing else. The task file itself, and an
    /// unrelated JSON file in the same folder, are not copies of a task file and are not
    /// reported, so a rule that flags every JSON file cannot satisfy the cases above.
    /// </summary>
    [Fact]
    public void TheTaskFileItselfAndOtherJsonAreNotKWDOCTASKS007()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile());
        fixture.Write("6-Docs/plans/notes.json", "{\n  \"note\": \"unrelated\"\n}\n");

        AssertDoesNotReport(Validate(fixture), SidecarTaskFile);
    }

    /// <summary>
    /// A copy under an <c>archive</c> folder inside the plans folder is within the plans folder but
    /// archived, so it is not reported; this pins the archive exclusion itself, not the folder test.
    /// </summary>
    [Fact]
    public void AnArchivedSidecarIsNotKWDOCTASKS007()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write("6-Docs/plans/archive/2026-11-05-archived-plan.tasks.json.new", "{}\n");

        AssertDoesNotReport(Validate(fixture), SidecarTaskFile);
    }

    /// <summary>
    /// The prompt fixture is the contract an architect copies, so its command has to run. This
    /// programme shipped prompts naming test classes that matched nothing — a filter that
    /// matches nothing exits zero and proves nothing — so the class is resolved from the prompt
    /// text, not from the fixture's own memory of what it wrote.
    /// </summary>
    [Fact]
    public void EveryCommandTheFixturePutsInAPromptResolvesToARealTestClass()
    {
        string[] classNames = [.. PlanTaskFileProbe.PromptClassNames()];

        Assert.NotEmpty(classNames);
        foreach (string className in classNames)
        {
            Type? resolved = typeof(PlanTaskFileValidatorTests).Assembly.GetType(className, throwOnError: false);
            Assert.True(
                resolved is not null,
                $"the task-file fixture's prompt runs '-class \"{className}\"', which matches no type in " +
                "KyberWeave.Tests, so that command would run zero tests and exit zero.");
        }
    }
}

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

        CapturedConsoleExecution<int> execution =
            ProcessConsoleCapture.Run(() => new DocsValidateCommand().Execute(null!, settings));
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
    internal const int SchemaVersion = 1;

    internal const string PlansFolder = "6-Docs/plans";
    internal const string PlanName = "2026-11-07-ready-tasks";
    internal const string PlanPath = PlansFolder + "/" + PlanName + ".md";
    internal const string TaskFilePath = PlansFolder + "/" + PlanName + ".tasks.json";
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

    /// <summary>The test class every fixture prompt names, read from the type it names.</summary>
    internal static string TestClass => typeof(PlanTaskFileValidatorTests).FullName!;

    /// <summary>The direct assembly runner command, fully qualified so it runs its class.</summary>
    internal static string RunnerCommand =>
        "dotnet tests/KyberWeave.Tests/bin/Release/net10.0/KyberWeave.Tests.dll -class \"" + TestClass + "\"";

    /// <summary>A corpus holding the plan index and the plan, and no task file.</summary>
    internal static DocFixture Corpus(string? planMarkdown = null)
    {
        return new DocFixture().WithCatalog()
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
            .Write(PlanPath, planMarkdown ?? PlanMarkdown());
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
        - **Required skills:** `csharp-dev`

        ### {{SecondTask}}: The drift and prompt rules

        - **Files:** {{Backticked(SecondTaskFiles)}}
        - **Depends on:** {{FirstTask}}
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
    internal static string TaskFile(
        string? planMarkdown = null,
        int schemaVersion = SchemaVersion,
        string? edit = null,
        string? missingPromptPart = null,
        string? promptPrefix = null)
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
            ["execution-digest"] = ExecutionDigest(plan),
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
            ["prompt"] = prompt
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
