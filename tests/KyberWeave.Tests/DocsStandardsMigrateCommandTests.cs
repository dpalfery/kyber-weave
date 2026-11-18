using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The standards migration renames a host's legacy <c>README.md</c> standards to their
/// prefixed names, writes the files a complete folder holds, and reshapes a standard whose
/// own headings are not the six the ontology requires without losing authored text. A second
/// run changes nothing.
/// </summary>
public class DocsStandardsMigrateCommandTests
{
    private static readonly OntologyConfig Declared = new()
    {
        Technologies = ["csharp"],
        Roles = ["reviewer"]
    };

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

    [Fact]
    public void ItRenamesTheStandardAndAddsTheMissingFiles()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        StandardsFolderMigrationResult result =
            new StandardsFolderMigration(repo.Path, Declared).Run();

        Assert.Equal(4, result.Changes.Count);
        Assert.True(File.Exists(StandardPath(repo.Path, "csharp")));
        Assert.False(File.Exists(Path.Combine(folder, "README.md")));
        Assert.Equal(LegacyCsharp, File.ReadAllText(StandardPath(repo.Path, "csharp")));
        Assert.True(File.Exists(Path.Combine(folder, "csharp.rules.md")));
        Assert.True(File.Exists(Path.Combine(folder, "csharp.skills.md")));
        Assert.True(File.Exists(Path.Combine(folder, "csharp.arbiter-rules.yml")));
    }

    [Fact]
    public void TheAddedStubsCarryTheFoldersOwnerAndScope()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        new StandardsFolderMigration(repo.Path, Declared).Run();

        string rules = File.ReadAllText(Path.Combine(folder, "csharp.rules.md"));
        Assert.Contains("doc-type: rule", rules, StringComparison.Ordinal);
        Assert.Contains("technology: csharp", rules, StringComparison.Ordinal);
        Assert.Contains("owner: dpalfery", rules, StringComparison.Ordinal);

        string arbiter = File.ReadAllText(Path.Combine(folder, "csharp.arbiter-rules.yml"));
        Assert.Contains("rules: []", arbiter, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondRunChangesNothing()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        new StandardsFolderMigration(repo.Path, Declared).Run();
        StandardsFolderMigrationResult second = new StandardsFolderMigration(repo.Path, Declared).Run();

        Assert.Empty(second.Changes);
    }

    [Fact]
    public void ADryRunWritesNothing()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        StandardsFolderMigrationResult result =
            new StandardsFolderMigration(repo.Path, Declared).Run(dryRun: true);

        Assert.Equal(4, result.Changes.Count);
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));
        Assert.False(File.Exists(StandardPath(repo.Path, "csharp")));
    }

    [Fact]
    public void ARoleFolderGetsRoleScopedStubs()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "reviewer");
        Directory.CreateDirectory(folder);

        new StandardsFolderMigration(repo.Path, Declared).Run();

        string standard = File.ReadAllText(StandardPath(repo.Path, "reviewer"));
        Assert.Contains("role: reviewer", standard, StringComparison.Ordinal);
        Assert.Contains("## Done checklist", standard, StringComparison.Ordinal);

        string skills = File.ReadAllText(Path.Combine(folder, "reviewer.skills.md"));
        Assert.Contains("doc-type: skill", skills, StringComparison.Ordinal);
        Assert.Contains("role: reviewer", skills, StringComparison.Ordinal);
    }

    [Fact]
    public void AUndeclaredFolderIsLeftAlone()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "python");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);

        StandardsFolderMigrationResult result =
            new StandardsFolderMigration(repo.Path, Declared).Run();

        Assert.Empty(result.Changes);
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));
    }

    /// <summary>
    /// A host standard that predates the six headings keeps every authored word: its own
    /// sections become third-level sections under the heading each fits, and each move is
    /// reported so the operator can see where the text went.
    /// </summary>
    [Fact]
    public void AStandardWithItsOwnHeadingsIsReshapedUnderTheSixAndEachMoveIsListed()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);

        StandardsFolderMigrationResult result =
            new StandardsFolderMigration(repo.Path, Declared).Run();

        string shaped = File.ReadAllText(StandardPath(repo.Path, "csharp"));

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
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);

        new StandardsFolderMigration(repo.Path, Declared).Run();
        string once = File.ReadAllText(StandardPath(repo.Path, "csharp"));

        StandardsFolderMigrationResult second =
            new StandardsFolderMigration(repo.Path, Declared).Run();

        Assert.Empty(second.Changes);
        Assert.Empty(second.Moves);
        Assert.Equal(once, File.ReadAllText(StandardPath(repo.Path, "csharp")));
    }

    [Fact]
    public void ADryRunListsTheMovesWithoutWriting()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);

        StandardsFolderMigrationResult result =
            new StandardsFolderMigration(repo.Path, Declared).Run(dryRun: true);

        Assert.Contains(result.Moves, move =>
            move.Heading == "Stack" && move.Under == "Coding standards");
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));
        Assert.False(File.Exists(StandardPath(repo.Path, "csharp")));
    }

    /// <summary>
    /// The operator reads the reshaped headings from the command's own output, not from a
    /// diff, so every moved section is printed with the heading it now sits under.
    /// </summary>
    [Fact]
    public void TheCommandPrintsEachMoveItMade()
    {
        using TempDirectory repo = new();
        string configDirectory = Path.Combine(repo.Path, ".kyber-weave");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "kyber-weave.yml"),
            "ontology:\n  docs-root: 6-Docs\n  technologies:\n    - csharp\n");
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
            CommandAppFixture.DocsStandardsMigrate().Run(
                ["docs", "standards", "migrate", repo.Path]));

        Assert.Equal(0, execution.Result);
        Assert.Contains(
            "moved 'Stack' under '## Coding standards'",
            execution.Output,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A second actor may create the standard between planning and applying. The rename is then
    /// skipped, and the skipped rename is not reported as one the migration made.
    /// </summary>
    [Fact]
    public void ARenameWhoseTargetAppearsAfterPlanningIsSkippedAndNotReported()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharp);
        StandardsFolderMigration migration = new(repo.Path, Declared);

        IReadOnlyList<StandardsFolderChange> planned = migration.Plan();
        File.WriteAllText(StandardPath(repo.Path, "csharp"), "written by someone else");

        List<StandardsFolderChange> applied = migration.Apply(planned, "csharp", null, null);

        Assert.DoesNotContain(applied, change => change.Kind == StandardsFolderChangeKind.Rename);
        Assert.Equal("written by someone else", File.ReadAllText(StandardPath(repo.Path, "csharp")));
        Assert.True(File.Exists(Path.Combine(folder, "README.md")));
    }

    /// <summary>
    /// The reshape text is derived from the legacy file, so a rename skipped because the target
    /// appeared must take its reshape with it instead of overwriting the second actor's standard.
    /// </summary>
    [Fact]
    public void AReshapeIsSkippedWhenItsRenameWasSkipped()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.md"), LegacyCsharpWithOwnHeadings);
        StandardsFolderMigration migration = new(repo.Path, Declared);

        IReadOnlyList<StandardsFolderChange> planned = migration.Plan();
        Assert.Contains(planned, change => change.Kind == StandardsFolderChangeKind.Reshape);
        File.WriteAllText(StandardPath(repo.Path, "csharp"), "written by someone else");

        List<StandardsFolderChange> applied = migration.Apply(planned, "csharp", null, null);

        Assert.DoesNotContain(applied, change => change.Kind == StandardsFolderChangeKind.Reshape);
        Assert.Equal("written by someone else", File.ReadAllText(StandardPath(repo.Path, "csharp")));
    }

    /// <summary>
    /// An add whose target appears between planning and applying is not clobbered, and is not
    /// reported as written.
    /// </summary>
    [Fact]
    public void AnAddWhoseTargetAppearsAfterPlanningIsSkippedAndNotReported()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        StandardsFolderMigration migration = new(repo.Path, Declared);

        IReadOnlyList<StandardsFolderChange> planned = migration.Plan();
        string rules = Path.Combine(folder, "csharp.rules.md");
        File.WriteAllText(rules, "theirs");

        List<StandardsFolderChange> applied = migration.Apply(planned, "csharp", null, null);

        Assert.DoesNotContain(applied, change => change.To.EndsWith("csharp.rules.md", StringComparison.Ordinal));
        Assert.Equal("theirs", File.ReadAllText(rules));
    }

    /// <summary>
    /// Writing the temporary is this tool's own I/O, not another actor's file appearing, so a
    /// failure there is reported and never answered with a skip. The temporary's name adds a dot, a
    /// GUID and <c>.tmp</c>, so a target name near the platform's limit makes the temporary's name
    /// too long while the target's own name is fine.
    /// </summary>
    [Fact]
    public void ATemporaryWriteFailureIsReportedRatherThanTreatedAsARace()
    {
        using TempDirectory repo = new();
        string scope = new('a', 220);
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", scope);
        Directory.CreateDirectory(folder);
        string target = $"{scope}.rules.md";

        Assert.ThrowsAny<IOException>(() => new StandardsFolderMigration(repo.Path, Declared).Apply(
            [
                new StandardsFolderChange(
                    StandardsFolderChangeKind.Add, string.Empty, $"6-Docs/standards/{scope}/{target}")
            ],
            scope,
            null,
            null));

        Assert.False(File.Exists(Path.Combine(folder, target)));
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
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        string rules = Path.Combine(folder, "csharp.rules.md");
        File.WriteAllText(rules, "theirs");
        UnixFileMode folderMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!TrySetMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute))
            Assert.Skip("A read-only directory needs a POSIX mode, so this case cannot be arranged here.");

        List<StandardsFolderChange> applied;
        try
        {
            applied = new StandardsFolderMigration(repo.Path, Declared).Apply(
                [new StandardsFolderChange(StandardsFolderChangeKind.Add, string.Empty, "6-Docs/standards/csharp/csharp.rules.md")],
                "csharp",
                null,
                null);
        }
        finally
        {
            TrySetMode(folder, folderMode);
        }

        Assert.Empty(applied);
        Assert.Equal("theirs", File.ReadAllText(rules));
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
        if (OperatingSystem.IsWindows())
            Assert.Skip("Creating a symbolic link needs a privilege this case cannot assume.");

        using TempDirectory repo = new();
        string shared = Path.Combine(repo.Path, "6-Docs", "shared", "csharp-standard.md");
        Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
        File.WriteAllText(shared, LegacyCsharpWithOwnHeadings);
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        File.CreateSymbolicLink(StandardPath(repo.Path, "csharp"), shared);

        IOException error = Assert.Throws<IOException>(() =>
            new StandardsFolderMigration(repo.Path, Declared).Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.NotNull(new FileInfo(StandardPath(repo.Path, "csharp")).LinkTarget);
        Assert.Equal(LegacyCsharpWithOwnHeadings, File.ReadAllText(shared));
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
        if (OperatingSystem.IsWindows())
            Assert.Skip("Comparing a file's mode needs a POSIX mode, so this case cannot be arranged here.");

        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        string legacy = Path.Combine(folder, "README.md");
        File.WriteAllText(legacy, LegacyCsharp);
        UnixFileMode hostMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (!TrySetMode(legacy, hostMode))
            Assert.Skip("Comparing a file's mode needs a POSIX mode, so this case cannot be arranged here.");

        new StandardsFolderMigration(repo.Path, Declared).Run();

        Assert.Equal(hostMode, ModeOf(StandardPath(repo.Path, "csharp")));
    }

    /// <summary>
    /// A standards folder kept as a symbolic link is refused: a migration writes stubs beside the
    /// host's standard and must not put them at the end of a link.
    /// </summary>
    [Fact]
    public void AFolderThatIsASymlinkOutsideTheRepositoryIsRefused()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Creating a symbolic link needs a privilege this case cannot assume.");

        using TempDirectory repo = new();
        using TempDirectory outside = new();
        Directory.CreateDirectory(Path.Combine(repo.Path, "6-Docs", "standards"));
        Directory.CreateSymbolicLink(
            Path.Combine(repo.Path, "6-Docs", "standards", "csharp"), outside.Path);

        IOException error = Assert.Throws<IOException>(() =>
            new StandardsFolderMigration(repo.Path, Declared).Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    /// <summary>
    /// Reshaping writes the standard in place, so a standard kept as a symbolic link out of the
    /// repository is refused rather than rewritten at its target.
    /// </summary>
    [Fact]
    public void AStandardThatIsASymlinkOutsideTheRepositoryIsNotReshaped()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Creating a symbolic link needs a privilege this case cannot assume.");

        using TempDirectory repo = new();
        using TempDirectory outside = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        string hostStandard = Path.Combine(outside.Path, "csharp.standards.md");
        File.WriteAllText(hostStandard, LegacyCsharpWithOwnHeadings);
        File.CreateSymbolicLink(StandardPath(repo.Path, "csharp"), hostStandard);

        IOException error = Assert.Throws<IOException>(() =>
            new StandardsFolderMigration(repo.Path, Declared).Run());

        Assert.Contains("symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal(LegacyCsharpWithOwnHeadings, File.ReadAllText(hostStandard));
    }

    /// <summary>
    /// A standard the folder holds but the process may not read is an error that names it, not a
    /// folder treated as having no standard and answered with a stub.
    /// </summary>
    [Fact]
    public void AStandardThatCannotBeReadIsReportedRatherThanReplacedByAStub()
    {
        using TempDirectory repo = new();
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        string standard = StandardPath(repo.Path, "csharp");
        File.WriteAllText(standard, LegacyCsharp);

        if (!TryMakeUnreadable(standard))
            Assert.Skip("An unreadable file needs a POSIX mode, so this case cannot be arranged here.");

        try
        {
            IOException error = Assert.Throws<IOException>(() =>
                new StandardsFolderMigration(repo.Path, Declared).Run());

            Assert.Contains("csharp.standards.md", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            RestoreReadable(standard);
        }
    }

    /// <summary>
    /// A standards file the process may not read is a diagnosable refusal, not a stack trace:
    /// the command names the failure and exits 1.
    /// </summary>
    [Fact]
    public void AnUnreadableStandardIsRefusedWithADiagnostic()
    {
        using TempDirectory repo = new();
        string configDirectory = Path.Combine(repo.Path, ".kyber-weave");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Combine(configDirectory, "kyber-weave.yml"),
            "ontology:\n  docs-root: 6-Docs\n  technologies:\n    - csharp\n");
        string folder = Path.Combine(repo.Path, "6-Docs", "standards", "csharp");
        Directory.CreateDirectory(folder);
        string standard = StandardPath(repo.Path, "csharp");
        File.WriteAllText(standard, LegacyCsharp);
        if (!TryMakeUnreadable(standard))
            Assert.Skip("An unreadable file needs a POSIX mode, so this case cannot be arranged here.");

        try
        {
            CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
                CommandAppFixture.DocsStandardsMigrate().Run(
                    ["docs", "standards", "migrate", repo.Path]));

            Assert.Equal(1, execution.Result);
            Assert.Contains("could not be read or written", execution.Output, StringComparison.Ordinal);
        }
        finally
        {
            RestoreReadable(standard);
        }
    }

    /// <summary>Sets a file's POSIX mode, answering false on a platform that has none.</summary>
    private static bool TrySetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return false;

        File.SetUnixFileMode(path, mode);
        return true;
    }

    /// <summary>Reads a file's POSIX mode, or null on a platform that has none.</summary>
    private static UnixFileMode? ModeOf(string path) =>
        OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

    /// <summary>Clears a file's read bits, answering false on a platform that has none.</summary>
    private static bool TryMakeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
            return false;

        File.SetUnixFileMode(path, UnixFileMode.None);
        return true;
    }

    /// <summary>Puts a file's read bits back so the temporary tree can be disposed.</summary>
    private static void RestoreReadable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static IReadOnlyList<string> LevelTwoHeadings(string text) =>
        [.. text.Split('\n')
            .Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Select(line => line[3..].Trim())];
}
