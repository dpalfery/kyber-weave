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
