using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The standards migration renames a host's legacy <c>README.md</c> standards to their
/// prefixed names and writes the files a complete folder holds, without changing a byte of
/// the text the host already wrote. A second run changes nothing.
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

        ## Types

        Use records for value-like data.
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
}
