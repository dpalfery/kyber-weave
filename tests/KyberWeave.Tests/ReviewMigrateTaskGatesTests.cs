using KyberWeave.Application.Review;
using KyberWeave.Cli;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The migration from agent-keyed task gates to technology-keyed ones: <c>kyber-weave review
/// migrate-task-gates</c> rewrites a host's <c>review.task-gates.&lt;agent&gt;</c> entries as
/// <c>review.technology-gates.&lt;technology&gt;</c>, shows the change before writing it, and
/// changes nothing else in the file.
/// </summary>
/// <remarks>
/// <para>
/// The old technology agents are being deleted, and a host's configuration outlives them. The shipped map keeps an
/// agent-keyed entry resolving for two minor releases; this command is how an operator moves it to the technology
/// key that survives. Every case here is about what an operator reads and what lands on disk, never about how the
/// rewrite is implemented.
/// </para>
/// <para>
/// The command is found by name in the CLI assembly rather than by a compiled reference, so the suite compiles
/// before the command exists and the first failure reads as "no command" — the reason the test contract for this
/// task names — rather than as an unexplained broken build.
/// </para>
/// <para>
/// The exact-text case is the strongest form of "changes nothing else": the whole file is asserted byte for byte,
/// so a rewrite that also reflows a comment, drops a blank line, or edits <c>review.gates</c> fails here.
/// </para>
/// </remarks>
public sealed class ReviewMigrateTaskGatesTests : IDisposable
{
    /// <summary>The command's name, which the reflection lookup and the registration case both use.</summary>
    private const string CommandName = "ReviewMigrateTaskGatesCommand";

    private const string MigrationCommand = "kyber-weave review migrate-task-gates";

    private readonly TempDirectory _root = new();

    public ReviewMigrateTaskGatesTests() => Directory.CreateDirectory(Repo);

    public void Dispose() => _root.Dispose();

    private string Repo => Path.Combine(_root.Path, "repo");

    private string ConfigPath => Path.Combine(Repo, ".kyber-weave", "kyber-weave.yml");

    // ----------------------------------------------------------------------------------------
    // The command exists and is reachable from the review branch
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The command has to exist before any behaviour below can be observed. This case names the missing piece on
    /// its own, so the RED reason reads as "no command".
    /// </summary>
    [Fact]
    public void TheMigrationCommandIsDeclaredBesideTheOtherReviewCommands()
    {
        Type command = MigrationCommandType();

        Assert.Equal(CommandName, command.Name);
        Assert.Equal("KyberWeave.Cli.Commands.Review", command.Namespace);
    }

    /// <summary>
    /// A host with no agent-keyed entry has nothing to migrate: the command says so, writes nothing, and exits 0.
    /// It is also the case that proves the command is registered on the real application, because every case here
    /// runs through it.
    /// </summary>
    [Fact]
    public void AHostWithNoAgentKeyedEntryWritesNothingAndExitsZero()
    {
        string before = WriteConfig("""
            review:
              gates:
                - id: build
                  run: [dotnet, build]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains("nothing to do", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    // ----------------------------------------------------------------------------------------
    // The preview, the write, and the bytes that do not move
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Without <c>--write</c> the run is a preview: it prints the removed and the added lines and writes not one
    /// byte, so the change is read before it exists.
    /// </summary>
    [Fact]
    public void ThePreviewPrintsTheRemovedAndTheAddedLinesAndWritesNothing()
    {
        string before = WriteConfig(TwoAgentsOnOneTechnology());

        CliRun run = Migrate();

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains($"-    csharp-dev:", run.Output, StringComparison.Ordinal);
        Assert.Contains($"+    csharp:", run.Output, StringComparison.Ordinal);
        Assert.Contains("--write", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two agents whose gate lists are equal collapse into one technology entry, and the file differs only inside
    /// the <c>review</c> block: every comment, blank line and other section is asserted byte for byte.
    /// </summary>
    [Fact]
    public void TwoEqualEntriesCollapseIntoOneTechnologyAndNothingElseInTheFileChanges()
    {
        WriteConfig(FileWithTwoEqualCsharpAgents());
        string before = File.ReadAllText(ConfigPath);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(ExpectedCollapsedFile, File.ReadAllText(ConfigPath));
        Assert.Contains("moved", run.Output, StringComparison.OrdinalIgnoreCase);

        // The positive control: the same run on the untouched copy is a preview, so the bytes above changed by
        // this command and not by the fixture.
        File.WriteAllText(ConfigPath, before);
        Assert.Equal(0, Migrate().ExitCode);
    }

    /// <summary>An agent that stays keeps its own entry, and only the removed agents move.</summary>
    [Fact]
    public void AnAgentThatStaysKeepsItsEntryAndOnlyARemovedAgentMoves()
    {
        WriteConfig("""
            review:
              task-gates:
                docs-dev:
                  - id: review-config
                    include: [".kyber-weave/kyber-weave.yml"]
                    run: [kyber-weave, review, catalogue, "."]
                go-dev:
                  - id: go-build
                    include: ["**/*.go"]
                    run: [go, build, ./...]
            """);

        Assert.Equal(0, Migrate("--write").ExitCode);

        string after = File.ReadAllText(ConfigPath);
        Assert.Contains("    docs-dev:", after, StringComparison.Ordinal);
        Assert.DoesNotContain("    go-dev:", after, StringComparison.Ordinal);

        // The Go agent is a removed one, so its gates are now read under the technology that outlives it.
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Repo);
        Assert.Equal(["go-build"], config.Review.TechnologyGates["go"].Select(gate => gate.Id));
        Assert.DoesNotContain("go-dev", config.Review.TaskGates.Keys);
        Assert.Equal(["review-config"], config.Review.TaskGates["docs-dev"].Select(gate => gate.Id));
    }

    /// <summary>
    /// A key may carry a trailing comment, which YAML ignores and a reader does not. The rewrite has to find the key
    /// anyway — and keep the comment, because the comment is the only record of why the entry exists.
    /// </summary>
    [Fact]
    public void AKeyCarryingATrailingCommentIsFoundAndItsCommentIsKept()
    {
        WriteConfig("""
            review:
              task-gates: # the per-agent gates
                csharp-dev: # the .NET list
                  - id: format
                    run: [dotnet, format]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(
            """
            review:
              technology-gates: # the per-agent gates
                csharp: # the .NET list
                  - id: format
                    run: [dotnet, format]
            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n",
            File.ReadAllText(ConfigPath));
    }

    /// <summary>A quoted key names the same agent as a bare one, and YAML readers agree, so the migration must too.</summary>
    [Fact]
    public void AQuotedAgentKeyIsMigrated()
    {
        WriteConfig("""
            review:
              task-gates:
                "csharp-dev":
                  - id: build
                    run: [dotnet, build]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Repo);
        Assert.Equal(["build"], config.Review.TechnologyGates["csharp"].Select(gate => gate.Id));
        Assert.Empty(config.Review.TaskGates);
    }

    // ----------------------------------------------------------------------------------------
    // The entries the command must not touch
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A removed agent with no technology in the shipped map cannot become a technology key. It is left exactly
    /// where it is — where it keeps resolving for two minor releases — and the run says why.
    /// </summary>
    [Fact]
    public void ARemovedAgentWithNoTechnologyIsLeftInPlaceAndReported()
    {
        string before = WriteConfig("""
            review:
              task-gates:
                test-dev:
                  - id: tests
                    include: ["**/*.cs"]
                    run: [dotnet, test]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains("test-dev", run.Output, StringComparison.Ordinal);
        Assert.Contains("technology", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A technology the host already declares is the host's own decision. The agent-keyed entry beside it is left
    /// for the operator to reconcile, and the run names both keys.
    /// </summary>
    [Fact]
    public void AnAgentWhoseTechnologyIsAlreadyDeclaredIsLeftInPlaceAndReported()
    {
        string before = WriteConfig("""
            review:
              technology-gates:
                csharp:
                  - id: test
                    run: [dotnet, test]
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains("csharp-dev", run.Output, StringComparison.Ordinal);
        Assert.Contains("technology-gates", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four agents share the <c>csharp</c> technology but a host's own gate lists may differ. Only equal lists
    /// collapse: an unequal pair is left for the operator, and the run names the technology both are waiting on.
    /// </summary>
    [Fact]
    public void TwoUnequalEntriesForOneTechnologyAreBothLeftInPlaceAndReported()
    {
        string before = WriteConfig("""
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
                dal-dev:
                  - id: build
                    run: [dotnet, build]
                  - id: test
                    run: [dotnet, test]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains("csharp", run.Output, StringComparison.Ordinal);
        Assert.Contains("differ", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A file whose configuration does not load is refused before anything is written: a rewrite based on a
    /// configuration nobody can read is a rewrite nobody can check.
    /// </summary>
    [Fact]
    public void AConfigurationThatDoesNotLoadIsRefusedWithZeroBytesWritten()
    {
        string before = WriteConfig("""
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
              gates:
                - id: build
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains("KW-CONFIG-001", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host may write its gate list once with YAML anchors and point later entries at it with aliases. Dropping an
    /// entry must not leave a dangling alias behind, so the migrated text is loaded before it is written; this case
    /// pins that the anchor an entry still in the file uses survives the rewrite.
    /// </summary>
    [Fact]
    public void TheMigratedFileStillResolvesEveryAnchorItsRemainingEntriesUse()
    {
        WriteConfig("""
            review:
              task-gates:
                csharp-dev:
                  - &format
                    id: format
                    include: ["**/*.cs"]
                    run: [dotnet, format, --verify-no-changes]
                dal-dev:
                  - *format
                maui-dev:
                  - *format
                test-dev:
                  - *format
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("moved", run.Output, StringComparison.OrdinalIgnoreCase);

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Repo);
        Assert.Equal(["format"], config.Review.TechnologyGates["csharp"].Select(gate => gate.Id));
        Assert.Equal(["format"], config.Review.TaskGates["test-dev"].Select(gate => gate.Id));
    }

    /// <summary>
    /// An anchor can be defined by an entry that folds away. If a later entry still points at it, the rewrite would
    /// leave a dangling alias and the file would stop loading, so it is refused with no byte written rather than
    /// leaving the host with a configuration nothing can read.
    /// </summary>
    [Fact]
    public void ARewriteThatWouldLeaveADanglingAliasIsRefusedWithZeroBytesWritten()
    {
        string before = WriteConfig("""
            review:
              task-gates:
                csharp-dev:
                  - id: format
                    run: [dotnet, format]
                dal-dev:
                  - &format
                    id: format
                    run: [dotnet, format]
                test-dev:
                  - *format
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Contains("would not load", run.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that already declares one technology gains the migrated entries in the block it wrote, rather than a
    /// second <c>technology-gates</c> key that no YAML document can hold twice.
    /// </summary>
    [Fact]
    public void AMigratedEntryJoinsTheTechnologyBlockTheHostAlreadyDeclares()
    {
        WriteConfig("""
            review:
              technology-gates:
                go:
                  - id: go-build
                    run: [go, build, ./...]
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        string after = File.ReadAllText(ConfigPath);
        Assert.Equal(1, after.Split("technology-gates:", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("task-gates:", after, StringComparison.Ordinal);

        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Repo);
        Assert.Equal(["go-build"], config.Review.TechnologyGates["go"].Select(gate => gate.Id));
        Assert.Equal(["build"], config.Review.TechnologyGates["csharp"].Select(gate => gate.Id));
    }

    /// <summary>
    /// YAML lets a host indent its children by any number of spaces, and a rewrite that assumed two would report
    /// nothing to do for a file it should have migrated.
    /// </summary>
    [Fact]
    public void AFileThatIndentsItsChildrenByFourSpacesIsMigratedToo()
    {
        WriteConfig("""
            review:
                task-gates:
                    go-dev:
                        - id: go-build
                          run: [go, build, ./...]
            """);

        CliRun run = Migrate("--write");

        Assert.Equal(0, run.ExitCode);
        KyberWeaveConfig config = KyberWeaveConfigLoader.Load(Repo);
        Assert.Equal(["go-build"], config.Review.TechnologyGates["go"].Select(gate => gate.Id));
        Assert.Empty(config.Review.TaskGates);
    }

    // ----------------------------------------------------------------------------------------
    // The warning that names this command
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Under the tiered roster an agent-keyed entry for a removed agent is a migration still to do. The warning
    /// names the command that does it, and the entry keeps resolving until it has been run.
    /// </summary>
    [Fact]
    public void AnEntryForARemovedAgentStillResolvesAndWarnsNamingTheMigrationCommand()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            worker-roster: tiered
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
            """);

        string warning = Assert.Single(config.Review.Warnings);
        Assert.Contains("csharp-dev", warning, StringComparison.Ordinal);
        Assert.Contains(MigrationCommand, warning, StringComparison.Ordinal);

        TaskGateResolution resolved = TaskGateResolver.Resolve(config.Review, "csharp-dev", "csharp");

        Assert.Equal(TaskGateSource.Agent, resolved.Source);
        Assert.Equal(["build"], resolved.Gates.Select(gate => gate.Id));
    }

    /// <summary>
    /// The technology roster is the shipped default and the one today's hosts run. Agent-keyed gates are its normal
    /// shape there, so a host is not told to migrate a configuration it is meant to keep.
    /// </summary>
    [Fact]
    public void TheTechnologyRosterWarnsAboutNoRemovedAgent()
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
            """);

        Assert.Empty(config.Review.Warnings);
    }

    // ----------------------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------------------

    private static Type MigrationCommandType()
    {
        Type? command = typeof(ReviewGatesCommand).Assembly
            .GetTypes()
            .FirstOrDefault(type => string.Equals(type.Name, CommandName, StringComparison.Ordinal));

        Assert.True(
            command is not null,
            $"The CLI assembly declares no '{CommandName}'. Task 5.3 adds " +
            "'src/KyberWeave.Cli/Commands/Review/ReviewMigrateTaskGatesCommand.cs' and registers it on the review " +
            "branch in CliApplication, so a host can rewrite its agent-keyed task gates as technology-keyed ones.");
        return command!;
    }

    /// <summary>Writes the repository's configuration with the line endings a committed file has, and returns it.</summary>
    private string WriteConfig(string yaml)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        string text = yaml.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        File.WriteAllText(ConfigPath, text);
        return text;
    }

    private CliRun Migrate(params string[] options)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CliApplication.Create().Run(["review", "migrate-task-gates", Repo, .. options]));
        return new CliRun(execution.Result, execution.Output);
    }

    private static string TwoAgentsOnOneTechnology() => """
        review:
          task-gates:
            csharp-dev:
              - id: format
                include: ["**/*.cs"]
                run: [dotnet, format, --verify-no-changes]
            dal-dev:
              - id: format
                include: ["**/*.cs"]
                run: [dotnet, format, --verify-no-changes]
        """;

    private static string FileWithTwoEqualCsharpAgents() => """
        # A host configuration.
        review:
          # The council's gates are not the task gates, and this migration does not touch them.
          gates:
            - id: build
              run: [dotnet, build]

          # .NET. Every .NET authoring role runs the same list.
          task-gates:
            csharp-dev:
              - id: format
                include: ["**/*.cs"]
                run: [dotnet, format, --verify-no-changes]
            dal-dev:
              - id: format
                include: ["**/*.cs"]
                run: [dotnet, format, --verify-no-changes]

        # A top-level setting that is not the migration's to change.
        autonomy: autonomous
        """;

    private static string ExpectedCollapsedFile => """
        # A host configuration.
        review:
          # The council's gates are not the task gates, and this migration does not touch them.
          gates:
            - id: build
              run: [dotnet, build]

          # .NET. Every .NET authoring role runs the same list.
          technology-gates:
            csharp:
              - id: format
                include: ["**/*.cs"]
                run: [dotnet, format, --verify-no-changes]

        # A top-level setting that is not the migration's to change.
        autonomy: autonomous
        """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";

    private sealed record CliRun(int ExitCode, string Output);
}
