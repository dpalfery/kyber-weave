using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The open-work and todo-inventory validators only protect anything if <c>docs validate</c> runs them, so the
/// wiring is pinned through the command over an in-memory checkout: the gate placement, the leftover task file and
/// the unlisted todo each fail the command and name the rule.
/// </summary>
public sealed class DocsValidateMergeGateTests
{
    private const string PlanIndex = "6-Docs/plans/README.md";
    private const string SpecIndex = "6-Docs/specs/README.md";
    private const string TodoIndex = "6-Docs/todo/README.md";

    /// <summary>A task file left behind when the change merges (<c>KW-DOC-LIFECYCLE-005</c>).</summary>
    private const string LeftoverTaskFile = "KW-DOC-LIFECYCLE-005";

    private sealed record Ran(int Result, string Output);

    /// <summary>What one <c>docs validate</c> run returned.</summary>
    private sealed record MergedRun(int Exit, string Output);

    private static Ran Run(DocFixture fixture, DocsValidateSettings settings)
    {
        TestConsole console = new() { EmitAnsiSequences = false };
        int exit = DocsTestCommands.Validate(DocsMemoryServicesD.Services(fixture), console).Execute(null!, settings);
        return new Ran(exit, console.Output);
    }

    private static string Index(string id) =>
        $"""
        ---
        id: {id}
        title: {id}
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-09-24
        ---

        # {id}

        None.
        """;

    private static string Work(string id, string docType) =>
        $"""
        ---
        id: {id}
        title: {id}
        doc-type: {docType}
        status: draft
        component: MotorcycleRAG API
        owner: API maintainers
        last-reviewed: 2026-09-24
        ---

        # {id}
        """;

    private static string TodoIndexDoc(string body) =>
        $"""
        ---
        id: todo/index
        title: Todo index
        doc-type: index
        status: current
        owner: Maintainers
        last-reviewed: 2026-09-13
        ---

        # Todo index

        {body}
        """;

    private static string Todo(string id) =>
        $"""
        ---
        id: {id}
        title: {id}
        doc-type: todo
        status: needs-review
        component: MotorcycleRAG API
        owner: API maintainers
        last-reviewed: 2026-09-13
        ---

        # {id}
        """;

    /// <summary>
    /// Open work is correct while it is being built, so the rule runs only when the merge
    /// gate asks for it; the default <c>docs validate</c> stays silent.
    /// </summary>
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void DocsValidateReportsOpenWorkOnlyWithMergeReady(bool mergeReady, int expectedExit)
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(PlanIndex, """
                ---
                id: plans/index
                title: Plans
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-09-24
                ---

                # Plans

                - [Kilo](2026-09-14-kilo.md)
                """)
            .Write("6-Docs/plans/2026-09-14-kilo.md", Work("plans/2026-09-14-kilo", "plan"));
        DocsValidateSettings settings = new DocsValidateSettings
        {
            Path = fixture.Root,
            Format = "json",
            MergeReady = mergeReady
        };

        Ran execution = Run(fixture, settings);

        Assert.Equal(expectedExit, execution.Result);
        Assert.Equal(mergeReady, execution.Output.Contains(OpenWorkValidator.OpenWork, StringComparison.Ordinal));
    }

    /// <summary>
    /// The worker prompts are duplicated from the plan into a tracked task file, and the plan is
    /// the authority, so the file has a shelf life of exactly one plan: the commit that archives
    /// the plan deletes it. One left behind describes work nobody is doing any more, and the
    /// next architect reads it as live.
    /// </summary>
    /// <remarks>
    /// The gate placement is part of the rule. An open plan is correct while the build runs, so
    /// this is a merge gate: the same corpus passes <c>docs validate</c> and fails
    /// <c>docs validate --merge-ready</c>.
    /// </remarks>
    [Fact]
    public void ALeftoverTaskFileFailsMergeReadyAsKWDOCCLIFECYCLE005()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(PlanIndex, """
                ---
                id: plans/index
                title: Plans
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-09-24
                ---

                # Plans

                - [Kilo](2026-09-14-kilo.md)
                """)
            .Write("6-Docs/plans/2026-09-14-kilo.md", Work("plans/2026-09-14-kilo", "plan"))
            .Write(
                "6-Docs/plans/2026-09-14-kilo.tasks.yml",
                """{ "schema-version": 1, "plan": "6-Docs/plans/2026-09-14-kilo.md", "tasks": [] }""");

        MergedRun withoutTheGate = ValidateThroughTheCommand(fixture, mergeReady: false);
        Assert.DoesNotContain(LeftoverTaskFile, withoutTheGate.Output, StringComparison.Ordinal);

        MergedRun withTheGate = ValidateThroughTheCommand(fixture, mergeReady: true);
        Assert.True(
            withTheGate.Output.Contains(LeftoverTaskFile, StringComparison.Ordinal),
            $"a merge carrying a leftover task file must report {LeftoverTaskFile}; it reported: {withTheGate.Output}");

        // The fixture is a plain directory, not a git repository, so the archive-closeout gate
        // also fails here. What this pins is that the leftover file is an error and not a
        // warning: it alone would already make the merge gate exit 1.
        Assert.Equal(1, withTheGate.Exit);
    }

    /// <summary>
    /// A specification's task file lives in the specification folder, so the closeout moves it
    /// with the folder. Looking only in the active folders would let a file ride into the
    /// archive beside its specification, where it stays readable and forever stale.
    /// </summary>
    [Fact]
    public void ATaskFileArchivedWithItsSpecificationIsStillCaught()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(SpecIndex, Index("specs/index"))
            .Write(
                "6-Docs/archive/specs/tray/tasks.yml",
                """{ "schema-version": 1, "plan": "6-Docs/archive/specs/tray/design.md", "tasks": [] }""");

        MergedRun run = ValidateThroughTheCommand(fixture, mergeReady: true);

        Assert.True(
            run.Output.Contains(LeftoverTaskFile, StringComparison.Ordinal),
            $"an archived specification carrying its task file must report {LeftoverTaskFile}; " +
            $"it reported: {run.Output}");
    }


    /// <summary>
    /// The validator only protects anything if <c>docs validate</c> actually runs it, so the
    /// wiring is pinned end to end: an unindexed todo fails the command and names the rule.
    /// </summary>
    [Fact]
    public void DocsValidateReportsAnUnlistedTodoUnderKWDOCLIFECYCLE002()
    {
        using DocFixture fixture = DocFixture.CreateInMemory().WithCatalog()
            .Write(TodoIndex, TodoIndexDoc("None."))
            .Write("6-Docs/todo/spike-cleanup.md", Todo("todo/spike-cleanup"));
        DocsValidateSettings settings = new DocsValidateSettings { Path = fixture.Root, Format = "json" };

        Ran execution = Run(fixture, settings);

        Assert.Contains(TodoInventoryValidator.UnlistedTodo, execution.Output, StringComparison.Ordinal);
        Assert.Equal(1, execution.Result);
    }

    private static MergedRun ValidateThroughTheCommand(DocFixture fixture, bool mergeReady)
    {
        DocsValidateSettings settings = new()
        {
            Path = fixture.Root,
            Format = "json",
            MergeReady = mergeReady
        };

        Ran execution = Run(fixture, settings);
        return new MergedRun(execution.Result, execution.Output);
    }

    /// <summary>What one <c>docs validate</c> child-in-process returned.</summary>
}
