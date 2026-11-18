using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The standards diff shows what a host folder is missing, what only the host has, and what
/// differs from the template. It changes nothing, and it treats owner, status and
/// last-reviewed as host choices rather than differences.
/// </summary>
public class DocsStandardsDiffCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly OntologyConfig Declared = new() { Technologies = ["csharp"] };

    private string PathOf(string relative) =>
        Path.Combine(_temp.Path, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Write(string relative, string content)
    {
        string full = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void ItReportsMissingChangedAndHostOnlyFiles()
    {
        KyberStandardsTemplates.TryGetPart(
            "csharp", KyberStandardsTemplates.Part.Standard, out string? standard);
        string changed = standard!.Replace("# C# coding standard", "# Our C# coding standard", StringComparison.Ordinal);
        Write("6-Docs/standards/csharp/csharp.standards.md", changed);
        Write("6-Docs/standards/csharp/extra.md", "host-owned note");

        StandardsDiffResult result = new StandardsTemplateDiff(_temp.Path, Declared).Run();

        StandardsFolderDiff folder = Assert.Single(result.Folders);
        Assert.Equal("csharp", folder.Name);
        Assert.Contains("csharp.standards.md", folder.Changed);
        Assert.Contains("csharp.rules.md", folder.Missing);
        Assert.Contains("extra.md", folder.HostOnly);
    }

    [Fact]
    public void AnOwnerStatusOrDateDifferenceIsNotAChange()
    {
        KyberStandardsTemplates.TryGetPart(
            "csharp", KyberStandardsTemplates.Part.Standard, out string? standard);
        string host = standard!
            .Replace("\nowner: unassigned\n", "\nowner: dpalfery\n", StringComparison.Ordinal)
            .Replace("\nstatus: draft\n", "\nstatus: current\n", StringComparison.Ordinal)
            .Replace("\nlast-reviewed: 2026-08-16\n", "\nlast-reviewed: 2026-11-16\n", StringComparison.Ordinal);
        Write("6-Docs/standards/csharp/csharp.standards.md", host);

        StandardsDiffResult result = new StandardsTemplateDiff(_temp.Path, Declared).Run();

        Assert.DoesNotContain("csharp.standards.md", Assert.Single(result.Folders).Changed);
    }

    [Fact]
    public void AnUnchangedFolderWithAllFourFilesIsClean()
    {
        foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
        {
            KyberStandardsTemplates.TryGetPart("csharp", part, out string? template);
            Write(
                $"6-Docs/standards/csharp/{KyberStandardsTemplates.FileName("csharp", part)}",
                template!);
        }

        StandardsFolderDiff folder = Assert.Single(new StandardsTemplateDiff(_temp.Path, Declared).Run().Folders);

        Assert.Empty(folder.Missing);
        Assert.Empty(folder.Changed);
        Assert.Empty(folder.HostOnly);
    }

    /// <summary>
    /// A body line that starts like a host-owned key is authored content, not a host choice, so
    /// editing it has to be reported as a change.
    /// </summary>
    [Fact]
    public void ABodyLineThatStartsWithAHostOwnedKeyIsStillCompared()
    {
        KyberStandardsTemplates.TryGetPart(
            "csharp", KyberStandardsTemplates.Part.Standard, out string? standard);
        string host = standard! + "\nstatus: review needed\n";
        Write("6-Docs/standards/csharp/csharp.standards.md", host);

        StandardsDiffResult result = new StandardsTemplateDiff(_temp.Path, Declared).Run();

        Assert.Contains("csharp.standards.md", Assert.Single(result.Folders).Changed);
    }

    /// <summary>
    /// The migration walks every declared docs root, so the diff has to as well: a folder that
    /// exists only under a secondary root is still reported rather than called clean.
    /// </summary>
    [Fact]
    public void AFolderUnderASecondaryDocsRootIsComparedToo()
    {
        OntologyConfig twoRoots = new()
        {
            Technologies = ["csharp"],
            DocsRoots = ["docs", "handbook"]
        };
        Write("handbook/standards/csharp/csharp.standards.md", "# ours\n");

        StandardsFolderDiff folder = Assert.Single(new StandardsTemplateDiff(_temp.Path, twoRoots).Run().Folders);

        Assert.Equal("handbook", folder.Root);
        Assert.Contains("csharp.rules.md", folder.Missing);
    }

    /// <summary>
    /// A standards file the process may not read is a diagnosable refusal, not a stack trace:
    /// the command names the failure and exits 1.
    /// </summary>
    [Fact]
    public void AnUnreadableStandardIsRefusedWithADiagnostic()
    {
        string configPath = PathOf(".kyber-weave/kyber-weave.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(
            configPath,
            "ontology:\n  docs-root: 6-Docs\n  technologies:\n    - csharp\n");
        Write("6-Docs/standards/csharp/csharp.standards.md", "---\nid: standards/csharp\n---\n");
        string standard = PathOf("6-Docs/standards/csharp/csharp.standards.md");

        if (!TryMakeUnreadable(standard))
            Assert.Skip("An unreadable file needs a POSIX mode, so this case cannot be arranged here.");

        try
        {
            CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
                CommandAppFixture.DocsStandardsDiff().Run(["docs", "standards", "diff", _temp.Path]));

            Assert.Equal(1, execution.Result);
            Assert.Contains("could not be read", execution.Output, StringComparison.Ordinal);
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

    /// <summary>
    /// The frontmatter block is the file's first line and the delimiter that closes it. A file
    /// whose first line is not a delimiter has no frontmatter at all, so a <c>---</c> further down
    /// is a rule or an example in the body: reading it as an opening delimiter would drop the
    /// lines under it from the comparison, hiding an edit to one of them.
    /// </summary>
    [Fact]
    public void ABodyDelimiterCannotOpenAFrontmatterBlockInAFileThatHasNone()
    {
        const string host = "# C# standard\n\nSome prose.\n\n---\n\nstatus: reviewed in the body\n";

        string normalized = StandardsTemplateDiff.Normalize(host);

        Assert.Contains("status: reviewed in the body", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameThatIsNotDeclaredIsRefusedWithTheNearestMatch()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new StandardsTemplateDiff(_temp.Path, Declared).Run("cshar"));

        Assert.Contains("csharp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclaredFolderThatIsNotInstalledIsSkipped()
    {
        OntologyConfig two = new() { Technologies = ["csharp", "python"] };
        Write("6-Docs/standards/csharp/csharp.standards.md", "x");

        StandardsDiffResult result = new StandardsTemplateDiff(_temp.Path, two).Run();

        Assert.Single(result.Folders);
        Assert.Equal("csharp", result.Folders[0].Name);
    }
}
