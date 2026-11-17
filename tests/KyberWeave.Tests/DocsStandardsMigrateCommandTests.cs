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
