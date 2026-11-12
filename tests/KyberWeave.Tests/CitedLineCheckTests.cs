using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The cited-line check moves a finding to the line that holds its excerpt, but only when the line the seat cited is
/// beyond the end of a file the repository really holds. The path comes from the seat's text, so the guards that keep
/// the check inside the repository are facts too: a path that leaves the repository is never read to move a finding.
/// </summary>
public sealed class CitedLineCheckTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _root;

    public CitedLineCheckTests()
    {
        _root = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(_root);
    }

    /// <summary>Deletes the temporary repository and the file beside it.</summary>
    public void Dispose()
    {
        _temp.Dispose();
    }

    /// <summary>
    /// A path with <c>..</c> leaves the repository. Reading the file it names would let a seat move a finding onto
    /// text from outside the review, so the finding is left as the seat wrote it.
    /// </summary>
    [Fact]
    public void ADotDotPathIsNeverReadToMoveAFinding()
    {
        WriteOutsideTheRepository("outside.cs", "int a;\nint b;\n");

        CitedLineResult result = CitedLineCheck.Check(_root, Finding("../outside.cs", line: 99, excerpt: "int b;"));

        Assert.Equal(CitedLineKind.Unverified, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.Equal(99, result.Finding.Line);
    }

    /// <summary>A rooted path names a file anywhere on the machine, so it is never read to move a finding.</summary>
    [Fact]
    public void ARootedPathIsNeverReadToMoveAFinding()
    {
        WriteOutsideTheRepository("outside.cs", "int a;\nint b;\n");
        string rooted = Path.Combine(_temp.Path, "outside.cs");

        CitedLineResult result = CitedLineCheck.Check(_root, Finding(rooted, line: 99, excerpt: "int b;"));

        Assert.Equal(CitedLineKind.Unverified, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.Equal(99, result.Finding.Line);
    }

    /// <summary>A cited file that does not exist is a finding the check cannot verify. It must say so, not throw.</summary>
    [Fact]
    public void AMissingFileIsNotReadAsAMoveAndDoesNotThrow()
    {
        CitedLineResult result = CitedLineCheck.Check(_root, Finding("src/Missing.cs", line: 99, excerpt: "int b;"));

        Assert.Equal(CitedLineKind.Unverified, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.Equal(99, result.Finding.Line);
    }

    /// <summary>A line beyond the end with no excerpt to find it by cannot be repaired, and the reason says why.</summary>
    [Fact]
    public void ABeyondTheEndLineWithNoExcerptIsUnresolvedWithAReason()
    {
        WriteRepositoryFile("src/Foo.cs", "a\nb\nc\n");

        CitedLineResult result = CitedLineCheck.Check(_root, Finding("src/Foo.cs", line: 99, excerpt: null));

        Assert.Equal(CitedLineKind.Unresolved, result.Kind);
        Assert.NotNull(result.Reason);
        Assert.Contains("excerpt", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A multi-line excerpt is found by its first non-blank line, trimmed. A seat that quotes several lines still
    /// moves the finding to the line where the quoted block begins.
    /// </summary>
    [Fact]
    public void AMultiLineExcerptIsFoundByItsFirstNonBlankLine()
    {
        WriteRepositoryFile("src/Foo.cs", "int a;\n    int total = 0;\n  return total;\n");

        CitedLineResult result = CitedLineCheck.Check(
            _root,
            Finding("src/Foo.cs", line: 99, excerpt: "\n   int total = 0;\n   return total;\n"));

        Assert.Equal(CitedLineKind.Relocated, result.Kind);
        Assert.Equal(2, result.Finding.Line);
        Assert.Equal(99, result.OriginalLine);
    }

    /// <summary>
    /// Line 0 can no more exist than a line past the end, so it is repaired the same way: the excerpt finds the line it
    /// means. Without the repair a line-0 finding is left as written and nothing is ever checked.
    /// </summary>
    [Fact]
    public void ALineZeroIsRepairedByItsExcerptLikeALineBeyondTheEnd()
    {
        WriteRepositoryFile("src/Foo.cs", "int a;\nint b;\n");

        CitedLineResult result = CitedLineCheck.Check(_root, Finding("src/Foo.cs", line: 0, excerpt: "int b;"));

        Assert.Equal(CitedLineKind.Relocated, result.Kind);
        Assert.Equal(2, result.Finding.Line);
    }

    /// <summary>A negative line with no excerpt cannot be repaired, the same as one beyond the end with none.</summary>
    [Fact]
    public void ANegativeLineWithNoExcerptIsUnresolved()
    {
        WriteRepositoryFile("src/Foo.cs", "a\nb\n");

        CitedLineResult result = CitedLineCheck.Check(_root, Finding("src/Foo.cs", line: -3, excerpt: null));

        Assert.Equal(CitedLineKind.Unresolved, result.Kind);
    }

    /// <summary>
    /// A path holding a NUL is a path no file system accepts. It is reported as unverified, with no exception and no crash
    /// through the verdict, and the message does not carry the path's absolute form.
    /// </summary>
    [Fact]
    public void ACitedPathWithANulCharacterIsUnverifiedAndDoesNotThrow()
    {
        WriteRepositoryFile("src/Foo.cs", "int a;\n");

        CitedLineResult result = CitedLineCheck.Check(_root, Finding("src/\0Foo.cs", line: 99, excerpt: "int a;"));

        Assert.Equal(CitedLineKind.Unverified, result.Kind);
        Assert.NotNull(result.Reason);
    }

    private static ReviewFinding Finding(string file, int line, string? excerpt) =>
        new(
            "correctness/off-by-one",
            "correctness",
            ReviewSeverity.Major,
            9,
            file,
            line,
            excerpt,
            "The loop bound excludes the final element.",
            "src/Foo.cs",
            "The last element is never processed.",
            "Use <= or count + 1.");

    private void WriteRepositoryFile(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteOutsideTheRepository(string name, string content)
    {
        File.WriteAllText(Path.Combine(_temp.Path, name), content);
    }
}
