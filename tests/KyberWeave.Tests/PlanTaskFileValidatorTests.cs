using System.Reflection;
using System.Text.Json;
using KyberWeave.Cli.Commands.Arbiter;
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

    /// <summary>
    /// The digest a task file records is the one the plan command reports, so the conductor can
    /// copy it from the command rather than recompute it. A reported digest validates clean; a
    /// wrong one is stale, which proves the check is live rather than passing every file.
    /// </summary>
    [Fact]
    public void ADigestReportedByArbiterPlanPassesKWDOCTASKS003()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string reported = ArbiterCliCommandTests.ReportedDigestOf(
            Path.Combine(fixture.Root, PlanTaskFileProbe.PlanPath.Replace('/', Path.DirectorySeparatorChar)));
        fixture.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile(digest: reported));

        AssertDoesNotReport(Validate(fixture), StaleDigest);

        using DocFixture control = PlanTaskFileProbe.Corpus();
        control.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile(digest: new string('0', 64)));

        AssertReports(Validate(control), StaleDigest);
    }

    /// <summary>
    /// A stale digest names the command that shows the plan's digest and the plan it belongs to,
    /// so the operator has something to run rather than a bare hash mismatch.
    /// </summary>
    [Fact]
    public void AStaleDigestHintNamesTheDigestCommand()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(PlanTaskFileProbe.TaskFilePath, PlanTaskFileProbe.TaskFile(digest: new string('0', 64)));

        ReportedFindings run = Validate(fixture);

        AssertReports(run, StaleDigest);
        Assert.Contains("kyber-weave arbiter plan", run.Output, StringComparison.Ordinal);
        Assert.Contains(PlanTaskFileProbe.PlanPath, run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A specification's task file is checked like a plan's: the digest the Arbiter's plan command
    /// reports for the spec's tasks.md validates clean. The control is what makes this a test of
    /// the rule rather than of silence: a validator that skips specifications reports nothing for
    /// either file, so only the stale control proves the spec file is read at all.
    /// </summary>
    [Fact]
    public void ASpecTaskFileWithTheDigestArbiterPlanReportsPasses()
    {
        using DocFixture fixture = SpecTaskFileProbe.Corpus();
        string reported = SpecTaskFileProbe.ReportedDigest(fixture);
        fixture.Write(SpecTaskFileProbe.TaskFilePath, SpecTaskFileProbe.TaskFile(digest: reported));

        AssertNoTaskFileFinding(Validate(fixture));

        using DocFixture control = SpecTaskFileProbe.Corpus();
        control.Write(SpecTaskFileProbe.TaskFilePath, SpecTaskFileProbe.TaskFile(digest: new string('0', 64)));

        AssertReports(Validate(control), StaleDigest);
    }

    /// <summary>
    /// A specification's digest that no longer matches its tasks.md is stale, and the finding names
    /// the command that shows the right digest and the spec task list it belongs to.
    /// </summary>
    [Fact]
    public void ASpecTaskFileWithAStaleDigestIsKWDOCTASKS003()
    {
        using DocFixture fixture = SpecTaskFileProbe.Corpus();
        fixture.Write(SpecTaskFileProbe.TaskFilePath, SpecTaskFileProbe.TaskFile(digest: new string('0', 64)));

        ReportedFindings run = Validate(fixture);

        AssertReports(run, StaleDigest);
        Assert.Contains("kyber-weave arbiter plan", run.Output, StringComparison.Ordinal);
        Assert.Contains(SpecTaskFileProbe.TasksPath, run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A spec task the file leaves out cannot be dispatched, so it is the same drift a plan's task
    /// file is checked for.
    /// </summary>
    [Fact]
    public void ASpecTaskFileMissingATaskIsKWDOCTASKS002()
    {
        using DocFixture accepted = SpecTaskFileProbe.Corpus();
        accepted.Write(SpecTaskFileProbe.TaskFilePath, SpecTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), DiffersFromPlan);

        using DocFixture missing = SpecTaskFileProbe.Corpus();
        missing.Write(
            SpecTaskFileProbe.TaskFilePath,
            SpecTaskFileProbe.TaskFile(edit: tasks => tasks.RemoveAt(1)));

        AssertReports(Validate(missing), DiffersFromPlan);
    }

    /// <summary>
    /// A task file whose <c>plan</c> field names another artifact of the same specification, here
    /// its design, does not belong to the tasks it sits beside.
    /// </summary>
    [Fact]
    public void ASpecTaskFileNamingAnotherArtifactIsKWDOCTASKS002()
    {
        using DocFixture accepted = SpecTaskFileProbe.Corpus();
        accepted.Write(SpecTaskFileProbe.TaskFilePath, SpecTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), DiffersFromPlan);

        using DocFixture drifted = SpecTaskFileProbe.Corpus();
        drifted.Write(
            SpecTaskFileProbe.TaskFilePath,
            SpecTaskFileProbe.TaskFile(planField: SpecTaskFileProbe.DesignPath));

        AssertReports(Validate(drifted), DiffersFromPlan);
    }

    /// <summary>
    /// A specification's task file that is not JSON is malformed, the same as a plan's.
    /// </summary>
    [Fact]
    public void AMalformedSpecTaskFileIsKWDOCTASKS001()
    {
        using DocFixture fixture = SpecTaskFileProbe.Corpus();
        fixture.Write(SpecTaskFileProbe.TaskFilePath, "{");

        AssertReports(Validate(fixture), Malformed);
    }

    /// <summary>
    /// A specification prompt with no runnable command is incomplete for the same reason a plan
    /// prompt is: the worker has nothing to verify its change with.
    /// </summary>
    [Fact]
    public void ASpecPromptWithoutACommandIsKWDOCTASKS004()
    {
        using DocFixture accepted = SpecTaskFileProbe.Corpus();
        accepted.Write(SpecTaskFileProbe.TaskFilePath, SpecTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), IncompletePrompt);

        using DocFixture incomplete = SpecTaskFileProbe.Corpus();
        incomplete.Write(
            SpecTaskFileProbe.TaskFilePath,
            SpecTaskFileProbe.TaskFile(edit: tasks => tasks[0]["prompt"] =
                "Implement task 1.1 in the files it names.\n\n" +
                "Acceptance criteria: the class reports a non-zero total, with no failures.\n"));

        AssertReports(Validate(incomplete), IncompletePrompt);
    }

    /// <summary>
    /// A specification prompt that sends the worker to read a spec file is the plan-read defect in
    /// the spec's own folder, so the rule that catches it for plans must cover specs too.
    /// </summary>
    [Fact]
    public void ASpecPromptThatOpensTheSpecIsKWDOCTASKS005()
    {
        using DocFixture accepted = SpecTaskFileProbe.Corpus();
        accepted.Write(SpecTaskFileProbe.TaskFilePath, SpecTaskFileProbe.TaskFile());
        AssertDoesNotReport(Validate(accepted), PlanReadInstruction);

        using DocFixture readsTheSpec = SpecTaskFileProbe.Corpus();
        readsTheSpec.Write(
            SpecTaskFileProbe.TaskFilePath,
            SpecTaskFileProbe.TaskFile(edit: tasks => tasks[0]["prompt"] =
                "Read 6-Docs/specs/ready-feature/design.md first.\n\n" + SpecTaskFileProbe.Prompt(SpecTaskFileProbe.FirstTask)));

        AssertReports(Validate(readsTheSpec), PlanReadInstruction);
    }

    /// <summary>
    /// A Ready specification with no task file beside its tasks.md is advised, as a Ready plan is.
    /// The spec's requirements and design are Ready too in this corpus, and they never need a task
    /// file of their own, so no warning may name them.
    /// </summary>
    [Fact]
    public void AReadySpecWithNoTaskFileWarnsKWDOCTASKS006()
    {
        using DocFixture fixture = SpecTaskFileProbe.Corpus();

        ReportedFindings run = Validate(fixture);

        AssertReports(run, MissingTaskFile);
        AssertNoMissingTaskFileNames(run, "requirements.md", "design.md");
    }

    /// <summary>
    /// The spec Ready line puts the colon inside the bold label, which the plan's Ready regex did
    /// not match. A plan written that way is Ready too, so it is advised when it has no task file.
    /// </summary>
    [Fact]
    public void APlanWithTheStatusColonOutsideTheBoldIsReady()
    {
        const string PlanReadyLine = "**Status: Ready.**";
        const string SpecReadyLine = "**Status:** Ready.";
        string plan = PlanTaskFileProbe.PlanMarkdown();
        Assert.Contains(PlanReadyLine, plan, StringComparison.Ordinal);
        string rewritten = plan.Replace(PlanReadyLine, SpecReadyLine, StringComparison.Ordinal);
        Assert.Contains(SpecReadyLine, rewritten, StringComparison.Ordinal);

        using DocFixture fixture = PlanTaskFileProbe.Corpus(rewritten);

        AssertReports(Validate(fixture), MissingTaskFile);
    }

    /// <summary>Asserts no task-file rule fired: the run's output carries no <c>KW-DOC-TASKS-*</c> code.</summary>
    private static void AssertNoTaskFileFinding(ReportedFindings run)
    {
        Assert.False(
            run.Output.Contains("KW-DOC-TASKS-", StringComparison.Ordinal),
            $"expected no KW-DOC-TASKS-* finding from 'docs validate'; it reported {run.Describe()}.");
    }

    /// <summary>
    /// Asserts no <c>KW-DOC-TASKS-006</c> finding names any of the given documents. It reads the
    /// JSON the command printed and checks each finding object, so a finding that names the spec's
    /// requirements or design in any field fails with the offending text.
    /// </summary>
    private static void AssertNoMissingTaskFileNames(ReportedFindings run, params string[] documentNames)
    {
        using JsonDocument document = ParseRunOutput(run.Output);
        Stack<JsonElement> pending = new();
        pending.Push(document.RootElement);
        while (pending.Count > 0)
        {
            JsonElement element = pending.Pop();
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    pending.Push(item);
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                bool isMissingTaskFile = element.EnumerateObject().Any(property =>
                    property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(property.Value.GetString(), MissingTaskFile, StringComparison.Ordinal));
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (isMissingTaskFile && property.Value.ValueKind == JsonValueKind.String)
                    {
                        string value = property.Value.GetString() ?? string.Empty;
                        foreach (string name in documentNames)
                        {
                            Assert.False(
                                value.Contains(name, StringComparison.Ordinal),
                                $"did not expect {MissingTaskFile} to name '{name}'; the finding was: {value}.");
                        }
                    }

                    pending.Push(property.Value);
                }
            }
        }
    }

    /// <summary>Parses the command's output, failing the test with the output named rather than throwing.</summary>
    private static JsonDocument ParseRunOutput(string output)
    {
        try
        {
            return JsonDocument.Parse(output);
        }
        catch (JsonException exception)
        {
            Assert.Fail($"'docs validate -f json' did not print one JSON document ({exception.Message}); output: {output}");
            throw;
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
    internal const string TaskFilePath = SpecFolder + "/tasks.json";
    internal const string DesignPath = SpecFolder + "/design.md";

    /// <summary>This fixture's two spec tasks, the second depending on the first.</summary>
    internal const string FirstTask = "1.1";
    internal const string SecondTask = "1.2";

    /// <summary>Each task's file scope, as both the task list and the task file declare it.</summary>
    internal const string FirstTaskFile = "tests/KyberWeave.Tests/PlanTaskFileValidatorTests.cs";
    internal const string SecondTaskFile = "src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>A corpus holding the specification index, the spec folder and its four files.</summary>
    internal static DocFixture Corpus(string? tasksMarkdown = null)
    {
        return new DocFixture().WithCatalog()
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
            .Write(TasksPath, tasksMarkdown ?? TasksMarkdown());
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
          - **Skills:** `test-dev`
          - **Depends on:** none
          - _Requirements: 1.1, 1.2_
        - [ ] 1.2 GREEN: Check the specification task file against its task list
          - **Objective:** Make the validator read the specification task file.
          - **Files:**
            - `src/KyberWeave.Core.Docs/Validation/PlanTaskFileValidator.cs`
          - **Acceptance:**
            - `Widget.Flag` is reported when a task is missing.
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

        return JsonSerializer.Serialize(file, JsonOptions);
    }

    /// <summary>
    /// The digest the Arbiter's plan command prints for the spec's <c>tasks.md</c>, read from the
    /// command's own JSON output, so the task file records the value a conductor would copy.
    /// </summary>
    internal static string ReportedDigest(DocFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        string path = Path.Combine(fixture.Root, TasksPath.Replace('/', Path.DirectorySeparatorChar));
        ArbiterPlanCommand command = new();
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
            ["prompt"] = prompt
        };
}
