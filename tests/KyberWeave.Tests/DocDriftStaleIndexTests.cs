using System.Diagnostics;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Processes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// An index older than the code is reported once, naming the fix, instead of as one unresolved
/// reference per symbol it has not seen yet.
/// </summary>
/// <remarks>
/// Uses a real git repository and a real index file written the way CodeGraph writes it, because
/// the evidence is the commit and file times the index recorded and what git says has changed since.
/// </remarks>
public sealed class DocDriftStaleIndexTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AnIndexMatchingTheCheckoutListsAnUnresolvedReferenceAsDrift()
    {
        string root = CreateRepositoryWithIndexedClass("Known");

        DiagnosticReport report = Drift(root, "Missing");

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal(DocDriftLinter.UnresolvedCodeRef, finding.Code);
    }

    [Fact]
    public void AnIndexOlderThanASourceFileReportsOneStaleIndexFindingNamingTheFix()
    {
        string root = CreateRepositoryWithIndexedClass("Known");
        File.WriteAllText(Path.Combine(root, "Added.cs"), "public class Added {}\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "add a class");

        DiagnosticReport report = Drift(root, "Added", "AlsoAdded");

        Diagnostic finding = Assert.Single(report.Items);
        Assert.Equal(DocDriftLinter.StaleIndex, finding.Code);
        Assert.Equal(Severity.Error, finding.Severity);
        Assert.Contains("Added.cs", finding.Message, StringComparison.Ordinal);
        Assert.Contains("codegraph sync .", finding.Hint, StringComparison.Ordinal);
        Assert.Contains("kyber-weave doctor --ensure .", finding.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentationOnlyChangeDoesNotMakeTheIndexStale()
    {
        string root = CreateRepositoryWithIndexedClass("Known");
        File.WriteAllText(Path.Combine(root, "NOTES.md"), "notes\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "docs only");

        DiagnosticReport report = Drift(root, "Missing");

        Assert.Equal(DocDriftLinter.UnresolvedCodeRef, Assert.Single(report.Items).Code);
    }

    [Fact]
    public void AnEditedIndexedFileMakesTheIndexStaleEvenWithoutACommit()
    {
        string root = CreateRepositoryWithIndexedClass("Known");
        string source = Path.Combine(root, "Known.cs");
        File.WriteAllText(source, "public class Renamed {}\n");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(5));

        DiagnosticReport report = Drift(root, "Renamed");

        Assert.Equal(DocDriftLinter.StaleIndex, Assert.Single(report.Items).Code);
    }

    [Fact]
    public void ANewCodeFileInAFolderTheIndexNeverCoveredIsNotStale()
    {
        string root = CreateRepositoryWithIndexedClass("Known");
        Directory.CreateDirectory(Path.Combine(root, "vendor", "lib"));
        File.WriteAllText(Path.Combine(root, "vendor", "lib", "Skipped.cs"), "public class Skipped {}\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "vendored code");

        DiagnosticReport report = Drift(root, "Missing");

        Assert.Equal(DocDriftLinter.UnresolvedCodeRef, Assert.Single(report.Items).Code);
    }

    [Fact]
    public void AnIndexWithNothingUnresolvedStaysQuietEvenWhenItLags()
    {
        string root = CreateRepositoryWithIndexedClass("Known");
        File.WriteAllText(Path.Combine(root, "Added.cs"), "public class Added {}\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "add a class");

        DiagnosticReport report = Drift(root, "Known");

        Assert.Empty(report.Items);
    }

    private static DiagnosticReport Drift(string root, params string[] symbols)
    {
        CodeGraphResolverAdapter resolver = CodeGraphResolverAdapter.ForRepository(root);
        Assert.True(resolver.IsAvailable, resolver.UnavailableReason);
        DocumentSet set = new()
        {
            Documents =
            [
                new DocumentModel
                {
                    RelativePath = "docs/reference/thing.md",
                    FilePath = Path.Combine(root, "docs/reference/thing.md"),
                    HasFrontmatter = true,
                    Frontmatter = new DocumentFrontmatter
                    {
                        Id = "reference/thing",
                        Title = "Thing",
                        DocType = "reference",
                        Status = "current",
                        Owner = "Maintainers",
                        LastReviewed = "2026-07-21",
                        CodeRefs = [.. symbols]
                    },
                    DocType = DocType.Reference,
                    Status = DocStatus.Current
                }
            ]
        };

        return new DocDriftLinter(resolver).Validate(set);
    }

    private string CreateRepositoryWithIndexedClass(string name)
    {
        string root = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(root);
        Git(root, "init", "-q", "-b", "main");
        string source = Path.Combine(root, name + ".cs");
        File.WriteAllText(source, $"public class {name} {{}}\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "init");
        string commit = Git(root, "rev-parse", "HEAD").Trim();
        long modified = new DateTimeOffset(File.GetLastWriteTimeUtc(source)).ToUnixTimeMilliseconds();

        Directory.CreateDirectory(Path.Combine(root, ".codegraph"));
        string database = Path.Combine(root, ".codegraph", "codegraph.db");
        string sql = $"""
            CREATE TABLE nodes (id TEXT, kind TEXT, name TEXT, qualified_name TEXT, file_path TEXT, language TEXT, start_line INTEGER, end_line INTEGER);
            CREATE TABLE edges (source TEXT, target TEXT, kind TEXT);
            CREATE TABLE files (path TEXT PRIMARY KEY, content_hash TEXT, language TEXT, size INTEGER, modified_at INTEGER, indexed_at INTEGER, node_count INTEGER);
            CREATE TABLE project_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at INTEGER);
            INSERT INTO nodes VALUES ('n1', 'class', '{name}', '{name}', '{name}.cs', 'csharp', 1, 1);
            INSERT INTO files VALUES ('{name}.cs', 'h', 'csharp', 10, {modified}.4766, {modified}, 1);
            INSERT INTO project_metadata VALUES ('indexed_at_commit', '{commit}', {modified});
            """;
        Run("sqlite3", root, [database, sql]);
        return root;
    }

    private static string Git(string directory, params string[] arguments)
    {
        string[] full = ["-c", "user.name=t", "-c", "user.email=t@example.com", .. arguments];
        return Run("git", directory, full);
    }

    private static string Run(string program, string directory, string[] arguments)
    {
        ProcessStartInfo startInfo = new(program)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(directory, ".no-global-gitconfig");
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, $"{program} {string.Join(' ', arguments)} failed: {result.StandardError}");
        return result.StandardOutput;
    }
}
