using KyberWeave.Tests.Arbiter;
using System.Reflection;
using System.Text.Json;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using Xunit;

using KyberWeave.Tests.Fakes;
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

    /// <summary>A task file larger than the validator reads is reported as one that could not be read, never loaded whole.</summary>
    [Fact]
    public void ATaskFileLargerThanTheValidatorReadsIsKWDOCTASKS001AndNamesWhy()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(PlanTaskFileProbe.TaskFilePath, new string(' ', (int)BoundedFile.MaxBytes + 1));

        ReportedFindings run = Validate(fixture);

        AssertReports(run, Malformed);
        Assert.Equal(1, run.Exit);
        Assert.Contains("could not be read", run.Output, StringComparison.Ordinal);
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
    /// A registry whose <c>plan-index</c> is a root-level file names no plans folder, so the
    /// sidecar walk has nothing to scope to. That is a state the run reports, not a clean result:
    /// silence would read as "no sidecars" when nothing was checked.
    /// </summary>
    [Fact]
    public void AnUnresolvablePlansFolderIsReportedAsAWarningNotSilence()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write("README.md", "# Plans\n");
        fixture.Write(".kyber-weave/kyber-weave.yml", "config-reg:\n  plan-index: README.md\n");

        ReportedFindings run = Validate(fixture);

        AssertReports(run, SidecarTaskFile);
        Assert.Contains("plans folder", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("WARNING", SeverityOf(run, SidecarTaskFile));
    }

    /// <summary>The severity the run printed for a code, read from its JSON findings.</summary>
    private static string? SeverityOf(ReportedFindings run, string rule)
    {
        using JsonDocument document = JsonDocument.Parse(run.Output);
        foreach (JsonElement finding in document.RootElement.GetProperty("findings").EnumerateArray())
        {
            if (finding.GetProperty("code").GetString() == rule)
            {
                return finding.GetProperty("severity").GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// The name alone does not make a file a copy of a task file. A file that happens to carry the
    /// suffix in its name but holds other content is not a sidecar, so it is not reported.
    /// </summary>
    [Fact]
    public void AFileWithTheSuffixInItsNameButOtherContentIsNotKWDOCTASKS007()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write("6-Docs/plans/notes.tasks.json.bak", "{\n  \"note\": \"unrelated\"\n}\n");

        AssertDoesNotReport(Validate(fixture), SidecarTaskFile);
    }

    /// <summary>
    /// A plans folder the sidecar walk cannot read is a reported state, not a crash that hides every
    /// other finding, and it does not stop the walk: a sidecar beside the unreadable folder is still
    /// found. The document loader walks the same folders first, so the loader reads the corpus while
    /// it is still readable and the folder is locked before the validator walks it, which is the
    /// race this guards: a folder that changes between the two walks.
    /// </summary>
    [Fact]
    public void AnUnreadablePlansSubfolderIsReportedAndTheWalkContinues()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are the only way to make a folder unreadable here.");
            return;
        }

        using DocFixture fixture = new();
        fixture.Write("6-Docs/plans/locked/placeholder.txt", "x");
        fixture.Write("6-Docs/plans/zz-later.tasks.json.new", PlanTaskFileProbe.TaskFile());
        OntologyConfig ontology = OntologyConfig.ProductDefaults;
        DocumentSet set = new DocumentLoader(fixture.Root, ontology).Load();
        KyberWeaveConfig config = KyberWeaveConfig.ProductDefaults.WithOntology(ontology);
        string locked = Path.Combine(fixture.Root, "6-Docs", "plans", "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.SkipWhen(CanList(locked), "Running with privileges that ignore file modes (such as root), so the folder stays readable.");

            DiagnosticReport report = new PlanTaskFileValidator(fixture.Root, config, DocsPorts.Files).Validate(set);

            Diagnostic warning = Assert.Single(report.Items, item => item.Code == SidecarTaskFile && item.Severity == Severity.Warning);
            Assert.Contains("could not be read", warning.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                report.Items,
                item => item.Code == SidecarTaskFile && item.Severity == Severity.Error
                        && item.Message.Contains("zz-later.tasks.json.new", StringComparison.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool CanList(string directory)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(directory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The content check reads the file's own top-level keys, not any text that mentions them. A
    /// note that quotes a schema key is not a task file.
    /// </summary>
    [Fact]
    public void AFileThatOnlyMentionsASchemaKeyInTextIsNotKWDOCTASKS007()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write(
            "6-Docs/plans/notes.tasks.json.bak",
            "Scratch note: the task file's \"execution-digest\" and \"schema-version\" keys are documented.\n");

        AssertDoesNotReport(Validate(fixture), SidecarTaskFile);
    }

    /// <summary>
    /// <c>schema-version</c> alone is a common key in unrelated JSON, so it does not make a file a
    /// task file; it counts only beside another key of the task file's schema.
    /// </summary>
    [Fact]
    public void UnrelatedJsonWithOnlyASchemaVersionKeyIsNotKWDOCTASKS007()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        fixture.Write("6-Docs/plans/other.tasks.json.bak", "{\n  \"schema-version\": 3,\n  \"name\": \"unrelated\"\n}\n");

        AssertDoesNotReport(Validate(fixture), SidecarTaskFile);
    }

    /// <summary>
    /// A copy whose schema key sits past the first few kilobytes is still a copy: the head that is
    /// read is large enough for a real task file's opening keys.
    /// </summary>
    [Fact]
    public void ASidecarWhoseDigestKeyFollowsALongPlanValueIsKWDOCTASKS007()
    {
        using DocFixture fixture = PlanTaskFileProbe.Corpus();
        string padding = new('x', 20000);
        fixture.Write(
            "6-Docs/plans/long.tasks.json.new",
            "{\n  \"plan\": \"" + padding + "\",\n  \"execution-digest\": \"abc\"\n}\n");

        AssertReports(Validate(fixture), SidecarTaskFile);
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
    /// The stale-digest hint pastes the plan path into a shell command, so the path is quoted by the
    /// house helper. A path with a space or a character a shell would interpret still pastes as written.
    /// </summary>
    [Theory]
    [InlineData("6-Docs/plans/plain.md")]
    [InlineData("6-Docs/plans/with space.md")]
    [InlineData("6-Docs/plans/$HOME.md")]
    [InlineData("6-Docs/plans/\"quoted\".md")]
    [InlineData("6-Docs/plans/`tick`.md")]
    [InlineData("6-Docs/plans/back\\slash.md")]
    public void ShellQuote_KeepsEveryPlanPathCharacterLiteral(string path)
    {
        string expected = OperatingSystem.IsWindows() ? "\"" + path + "\"" : "'" + path + "'";
        Assert.Equal(expected, ShellQuote.Quote(path));
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
        Assert.Contains("kyber-weave docs tasks", run.Output, StringComparison.Ordinal);
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
        Assert.Contains("kyber-weave docs tasks", run.Output, StringComparison.Ordinal);
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
