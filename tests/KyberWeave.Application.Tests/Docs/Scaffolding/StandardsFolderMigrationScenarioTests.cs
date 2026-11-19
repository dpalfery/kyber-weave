using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// The standards migration renames a host's legacy <c>README.md</c> standards to their
/// prefixed names, writes the files a complete folder holds, and reshapes a standard whose
/// own headings are not the six the ontology requires without losing authored text. A second
/// run changes nothing.
/// </summary>
public class StandardsFolderMigrationScenarioTests
{
    private readonly MemoryDocsHost _host = new();

    private static readonly KyberWeaveConfig Declared = KyberWeaveConfigDefaults.Product.WithOntology(new()
    {
        Technologies = ["csharp"],
        Roles = ["reviewer"]
    });

    private const string LegacyCsharp = """
        ---
        id: standards/csharp
        title: C# standard
        doc-type: coding-standard
        status: current
        technology: csharp
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# standard

        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist
        """;

    private const string LegacyCsharpWithOwnHeadings = """
        ---
        id: standards/csharp
        title: C# standard
        doc-type: coding-standard
        status: current
        technology: csharp
        owner: dpalfery
        last-reviewed: 2026-11-16
        ---

        # C# standard

        How C# is written in this repository.

        ## Stack

        - .NET 10.

        ## Naming

        Types use PascalCase.

        ## Commands

        Run `dotnet build`.
        """;

    private static string StandardPath(string root, string scope) =>
        Path.Combine(root, "6-Docs", "standards", scope, $"{scope}.standards.md");

    private StandardsFolderMigration Migration(string repoRoot) =>
        new(_host.Files, repoRoot, Declared, _host.Paths, _host.Environment, _host.Ids);

    [Fact]
    public void ItRenamesTheStandardAndAddsTheMissingFiles()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        StandardsFolderMigrationResult result =
            Migration(_host.Repo).Run();

        Assert.Equal(4, result.Changes.Count);
        Assert.True(_host.Files.FileExists(StandardPath(_host.Repo, "csharp")));
        Assert.False(_host.Files.FileExists(Path.Combine(folder, "README.md")));
        Assert.Equal(LegacyCsharp, _host.Files.ReadAllText(StandardPath(_host.Repo, "csharp")));
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "csharp.rules.md")));
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "csharp.skills.md")));
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "csharp.arbiter-rules.yml")));
    }

    [Fact]
    public void TheAddedStubsCarryTheFoldersOwnerAndScope()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        Migration(_host.Repo).Run();

        string rules = _host.Files.ReadAllText(Path.Combine(folder, "csharp.rules.md"));
        Assert.Contains("doc-type: rule", rules, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", rules, StringComparison.Ordinal);
        Assert.Contains("owner: dpalfery", rules, StringComparison.Ordinal);

        string arbiter = _host.Files.ReadAllText(Path.Combine(folder, "csharp.arbiter-rules.yml"));
        Assert.Contains("rules: []", arbiter, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondRunChangesNothing()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        Migration(_host.Repo).Run();
        StandardsFolderMigrationResult second = Migration(_host.Repo).Run();

        Assert.Empty(second.Changes);
    }

    [Fact]
    public void ADryRunWritesNothing()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        StandardsFolderMigrationResult result =
            Migration(_host.Repo).Run(dryRun: true);

        Assert.Equal(4, result.Changes.Count);
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "README.md")));
        Assert.False(_host.Files.FileExists(StandardPath(_host.Repo, "csharp")));
    }

    [Fact]
    public void ARoleFolderGetsRoleScopedStubs()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "reviewer");
        _host.Files.CreateDirectory(folder);

        Migration(_host.Repo).Run();

        string standard = _host.Files.ReadAllText(StandardPath(_host.Repo, "reviewer"));
        Assert.Contains("role: reviewer", standard, StringComparison.Ordinal);
        Assert.Contains("## Done checklist", standard, StringComparison.Ordinal);

        string skills = _host.Files.ReadAllText(Path.Combine(folder, "reviewer.skills.md"));
        Assert.Contains("doc-type: skill", skills, StringComparison.Ordinal);
        Assert.Contains("role: reviewer", skills, StringComparison.Ordinal);
    }

    [Fact]
    public void AUndeclaredFolderIsLeftAlone()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "python");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        StandardsFolderMigrationResult result =
            Migration(_host.Repo).Run();

        Assert.Empty(result.Changes);
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "README.md")));
    }

    /// <summary>
    /// A host standard that predates the six headings keeps every authored word: its own
    /// sections become third-level sections under the heading each fits, and each move is
    /// reported so the operator can see where the text went.
    /// </summary>
    [Fact]
    public void AStandardWithItsOwnHeadingsIsReshapedUnderTheSixAndEachMoveIsListed()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);

        StandardsFolderMigrationResult result =
            Migration(_host.Repo).Run();

        string shaped = _host.Files.ReadAllText(StandardPath(_host.Repo, "csharp"));

        Assert.Equal(DocsLayout.StandardHeadings, LevelTwoHeadings(shaped));
        Assert.Contains("### Stack", shaped, StringComparison.Ordinal);
        Assert.Contains("### Naming", shaped, StringComparison.Ordinal);
        Assert.Contains("How C# is written in this repository.", shaped, StringComparison.Ordinal);
        Assert.Contains("- .NET 10.", shaped, StringComparison.Ordinal);
        Assert.Contains("Types use PascalCase.", shaped, StringComparison.Ordinal);
        Assert.Contains("Run `dotnet build`.", shaped, StringComparison.Ordinal);

        Assert.Contains(result.Moves, move =>
            move.Heading == "Stack" && move.Under == "Coding standards");
        Assert.Contains(result.Moves, move =>
            move.Heading == "Naming" && move.Under == "Naming and layout");
        Assert.DoesNotContain(result.Moves, move => move.Heading == "Commands");
    }

    [Fact]
    public void ReshapingChangesNothingOnASecondRun()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);

        Migration(_host.Repo).Run();
        string once = _host.Files.ReadAllText(StandardPath(_host.Repo, "csharp"));

        StandardsFolderMigrationResult second =
            Migration(_host.Repo).Run();

        Assert.Empty(second.Changes);
        Assert.Empty(second.Moves);
        Assert.Equal(once, _host.Files.ReadAllText(StandardPath(_host.Repo, "csharp")));
    }

    [Fact]
    public void ADryRunListsTheMovesWithoutWriting()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);

        StandardsFolderMigrationResult result =
            Migration(_host.Repo).Run(dryRun: true);

        Assert.Contains(result.Moves, move =>
            move.Heading == "Stack" && move.Under == "Coding standards");
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "README.md")));
        Assert.False(_host.Files.FileExists(StandardPath(_host.Repo, "csharp")));
    }

    /// <summary>
    /// A second actor may create the standard between planning and applying. The rename is then
    /// skipped, and the skipped rename is not reported as one the migration made.
    /// </summary>
    [Fact]
    public void ARenameWhoseTargetAppearsAfterPlanningIsSkippedAndNotReported()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);
        StandardsFolderMigration migration = Migration(_host.Repo);

        IReadOnlyList<StandardsFolderChange> planned = migration.Plan();
        _host.Files.WriteAllText(StandardPath(_host.Repo, "csharp"), "written by someone else");

        List<StandardsFolderChange> applied = migration.Apply(planned, "csharp", null, null);

        Assert.DoesNotContain(applied, change => change.Kind == StandardsFolderChangeKind.Rename);
        Assert.Equal("written by someone else", _host.Files.ReadAllText(StandardPath(_host.Repo, "csharp")));
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "README.md")));
    }

    /// <summary>
    /// The reshape text is derived from the legacy file, so a rename skipped because the target
    /// appeared must take its reshape with it instead of overwriting the second actor's standard.
    /// </summary>
    [Fact]
    public void AReshapeIsSkippedWhenItsRenameWasSkipped()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);
        StandardsFolderMigration migration = Migration(_host.Repo);

        IReadOnlyList<StandardsFolderChange> planned = migration.Plan();
        Assert.Contains(planned, change => change.Kind == StandardsFolderChangeKind.Reshape);
        _host.Files.WriteAllText(StandardPath(_host.Repo, "csharp"), "written by someone else");

        List<StandardsFolderChange> applied = migration.Apply(planned, "csharp", null, null);

        Assert.DoesNotContain(applied, change => change.Kind == StandardsFolderChangeKind.Reshape);
        Assert.Equal("written by someone else", _host.Files.ReadAllText(StandardPath(_host.Repo, "csharp")));
    }

    /// <summary>
    /// An add whose target appears between planning and applying is not clobbered, and is not
    /// reported as written.
    /// </summary>
    [Fact]
    public void AnAddWhoseTargetAppearsAfterPlanningIsSkippedAndNotReported()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        StandardsFolderMigration migration = Migration(_host.Repo);

        IReadOnlyList<StandardsFolderChange> planned = migration.Plan();
        string rules = Path.Combine(folder, "csharp.rules.md");
        _host.Files.WriteAllText(rules, "theirs");

        List<StandardsFolderChange> applied = migration.Apply(planned, "csharp", null, null);

        Assert.DoesNotContain(applied, change => change.To.EndsWith("csharp.rules.md", StringComparison.Ordinal));
        Assert.Equal("theirs", _host.Files.ReadAllText(rules));
    }

    /// <summary>
    /// Writing the temporary is this tool's own I/O, not another actor's file appearing, so a
    /// failure there is reported and never answered with a skip. The fake file system fails every
    /// write to a <c>.tmp</c> name, the way a name past the platform's limit fails on a disk.
    /// </summary>
    [Fact]
    public void ATemporaryWriteFailureIsReportedRatherThanTreatedAsARace()
    {
        const string scope = "csharp";
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", scope);
        _host.Files.CreateDirectory(folder);
        string target = $"{scope}.rules.md";
        _host.Files.FailWhen(operation =>
            operation.Path is { } path
            && path.EndsWith(".tmp", StringComparison.Ordinal)
            && !operation.Name.StartsWith("Read", StringComparison.Ordinal)
            && operation.Name is not ("FileExists" or "DirectoryExists" or "PathExists"));

        Assert.ThrowsAny<IOException>(() => Migration(_host.Repo).Apply(
            [
                new StandardsFolderChange(
                    StandardsFolderChangeKind.Add, string.Empty, $"6-Docs/standards/{scope}/{target}")
            ],
            scope,
            null,
            null));

        Assert.False(_host.Files.FileExists(Path.Combine(folder, target)));
    }

    /// <summary>
    /// A target that already holds a file makes the change a no-op before anything is written, so
    /// the outcome does not depend on what a write attempt would have done: no temporary is created,
    /// and a directory the process may not write to is never touched. That is also what keeps the
    /// move's own failure readable: with the target absent beforehand, a failure while a file exists
    /// afterwards means the file appeared in between, not that an I/O error was masked as a race.
    /// </summary>
    [Fact]
    public void AnAddWhoseTargetAlreadyExistsIsSkippedBeforeAnyWrite()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        string rules = Path.Combine(folder, "csharp.rules.md");
        _host.Files.WriteAllText(rules, "theirs");
        _host.Files.FailWhen(operation =>
            operation.Path is { } path
            && path.StartsWith(folder, StringComparison.Ordinal)
            && (operation.Name.StartsWith("Write", StringComparison.Ordinal)
                || operation.Name is nameof(InMemoryFileSystem.MoveFile) or nameof(InMemoryFileSystem.CreateNewFile)
                    or nameof(InMemoryFileSystem.TryCreateNewFile) or nameof(InMemoryFileSystem.OpenWriteNew)
                    or nameof(InMemoryFileSystem.DeleteFile)));

        List<StandardsFolderChange> applied = Migration(_host.Repo).Apply(
            [new StandardsFolderChange(StandardsFolderChangeKind.Add, string.Empty, "6-Docs/standards/csharp/csharp.rules.md")],
            "csharp",
            null,
            null);

        Assert.Empty(applied);
        Assert.Equal("theirs", _host.Files.ReadAllText(rules));
    }

    /// <summary>
    /// A symbolic link sitting where the migration writes is refused, not replaced by a regular
    /// file. The migration refuses links everywhere else, and a reshape that quietly turned a
    /// host's link into a plain file would leave the host's own copy behind, no longer the file
    /// the corpus reads.
    /// </summary>
    [Fact]
    public void AStandardKeptInsideTheRepositoryAsASymbolicLinkIsRefusedNotReplaced()
    {
        string shared = Path.Combine(_host.Repo, "6-Docs", "shared", "csharp-standard.md");
        _host.Files.CreateDirectory(Path.GetDirectoryName(shared)!);
        _host.Files.WriteAllText(shared, LegacyCsharpWithOwnHeadings);
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.CreateFileLink(StandardPath(_host.Repo, "csharp"), shared);

        IOException error = Assert.Throws<IOException>(() =>
            Migration(_host.Repo).Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.NotNull(_host.Files.GetLinkTarget(StandardPath(_host.Repo, "csharp")));
        Assert.Equal(LegacyCsharpWithOwnHeadings, _host.Files.ReadAllText(shared));
    }

    /// <summary>
    /// The rename is one move, not a copy followed by a delete: the prefixed standard keeps the
    /// file it was — its bytes, its mode and its identity — and there is no moment at which both
    /// names hold the standard. A copy would leave the source behind until the delete succeeds, and
    /// a crash between the two leaves two standards for one scope with nothing left to report it.
    /// </summary>
    [Fact]
    public void TheRenameMovesTheStandardRatherThanCopyingIt()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        string legacy = Path.Combine(folder, "README.md");
        _host.Files.WriteAllText(legacy, LegacyCsharp);
        const FileAttributes hostAttributes = FileAttributes.Hidden;
        _host.Files.SetAttributes(legacy, hostAttributes);
        string standard = StandardPath(_host.Repo, "csharp");
        List<FileSystemOperation> operations = [];
        _host.Files.FailWhen(operation =>
        {
            operations.Add(operation);
            return false;
        });

        Migration(_host.Repo).Run();

        Assert.Equal(hostAttributes, _host.Files.GetAttributes(standard) & hostAttributes);
        Assert.Contains(operations, operation =>
            operation.Name == nameof(InMemoryFileSystem.MoveFile)
            && operation.Path == legacy
            && operation.DestinationPath == standard);
        Assert.DoesNotContain(operations, operation =>
            operation.Path == standard && operation.Name.StartsWith("Write", StringComparison.Ordinal));
        Assert.False(_host.Files.FileExists(legacy));
    }

    /// <summary>
    /// A standards folder kept as a symbolic link is refused: a migration writes stubs beside the
    /// host's standard and must not put them at the end of a link.
    /// </summary>
    [Fact]
    public void AFolderThatIsASymlinkOutsideTheRepositoryIsRefused()
    {
        string outside = Path.Combine(_host.Files.Root, "outside");
        _host.Files.CreateDirectory(outside);
        _host.Files.CreateDirectory(Path.Combine(_host.Repo, "6-Docs", "standards"));
        _host.Files.CreateDirectoryLink(
            Path.Combine(_host.Repo, "6-Docs", "standards", "csharp"), outside);

        IOException error = Assert.Throws<IOException>(() =>
            Migration(_host.Repo).Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Empty(_host.Files.EnumerateEntries(outside));
    }

    /// <summary>
    /// Reshaping writes the standard in place, so a standard kept as a symbolic link out of the
    /// repository is refused rather than rewritten at its target.
    /// </summary>
    [Fact]
    public void AStandardThatIsASymlinkOutsideTheRepositoryIsNotReshaped()
    {
        string outside = Path.Combine(_host.Files.Root, "outside");
        _host.Files.CreateDirectory(outside);
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        string hostStandard = Path.Combine(outside, "csharp.standards.md");
        _host.Files.WriteAllText(hostStandard, LegacyCsharpWithOwnHeadings);
        _host.Files.CreateFileLink(StandardPath(_host.Repo, "csharp"), hostStandard);

        IOException error = Assert.Throws<IOException>(() =>
            Migration(_host.Repo).Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal(LegacyCsharpWithOwnHeadings, _host.Files.ReadAllText(hostStandard));
    }

    /// <summary>
    /// A standard the folder holds but the process may not read is an error that names it, not a
    /// folder treated as having no standard and answered with a stub.
    /// </summary>
    [Fact]
    public void AStandardThatCannotBeReadIsReportedRatherThanReplacedByAStub()
    {
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        string standard = StandardPath(_host.Repo, "csharp");
        _host.Files.WriteAllText(standard, LegacyCsharp);
        _host.Files.FailWhen(
            operation => string.Equals(operation.Path, standard, StringComparison.Ordinal)
                && (operation.Name.StartsWith("Read", StringComparison.Ordinal) || operation.Name == "OpenRead"),
            new UnauthorizedAccessException($"Access to the path '{standard}' is denied."));

        IOException error = Assert.Throws<IOException>(() =>
            Migration(_host.Repo).Run());

        Assert.Contains("csharp.standards.md", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The registry block is re-rendered in place, so an <c>AGENTS.md</c> kept as a symbolic
    /// link is refused rather than rewritten at its target, and the refusal happens before a
    /// single standard moves.
    /// </summary>
    [Fact]
    public void AnAgentsFileKeptAsASymbolicLinkIsRefusedBeforeAnyStandardMoves()
    {
        string shared = Path.Combine(_host.Repo, "shared", "agents.md");
        _host.Files.CreateDirectory(Path.GetDirectoryName(shared)!);
        _host.Files.WriteAllText(
            shared,
            "<!-- KYBER_WEAVE_CONFIG_REG_START -->\n" +
            "- **<csharp-coding-standard>**: `6-Docs/standards/csharp/README.md`\n" +
            "<!-- KYBER_WEAVE_CONFIG_REG_END -->\n");
        _host.Files.CreateFileLink(Path.Combine(_host.Repo, "AGENTS.md"), shared);
        string folder = Path.Combine(_host.Repo, "6-Docs", "standards", "csharp");
        _host.Files.CreateDirectory(folder);
        _host.Files.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        IOException error = Assert.Throws<IOException>(() => Migration(_host.Repo).Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.True(_host.Files.FileExists(Path.Combine(folder, "README.md")));

        // The refusal precedes every write: no prefixed standard and no stub was added beside the
        // legacy file, which is what proves the check is not merely early in one folder's plan.
        Assert.Equal(
            ["README.md"],
            _host.Files.EnumerateFiles(folder, "*", recursive: false)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(
            "README.md", _host.Files.ReadAllText(shared), StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> LevelTwoHeadings(string text) =>
        [.. text.Split('\n')
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())];
}
