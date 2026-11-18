using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Paths;

/// <summary>
/// Pins containment when one directory holds two siblings whose names differ only in case.
/// </summary>
/// <remarks>
/// A Windows volume compares names case-insensitively by default, yet a single directory can have case sensitivity
/// turned on, and there two siblings may differ only in case. The resolver's case rule describes the volume, not
/// that directory, so containment must be decided by the spelling each entry reports rather than by folding the two
/// names together: otherwise a link into the sibling looks contained and a deployment writes outside its root.
/// The in-memory file system keeps both siblings, so this test catches the escape on every platform, including the
/// macOS machine where the Windows integration test cannot run.
/// </remarks>
public sealed class CaseDistinctSiblingPathTests
{
    private const string CaseParent = "/case-distinct-parent";
    private const string UpperRoot = CaseParent + "/Root";
    private const string LowerSibling = CaseParent + "/root";

    [Fact]
    public void IsWithinRefusesALinkIntoACaseDistinctSibling()
    {
        FileSystemPathResolver resolver = WindowsVolumeWithCaseSensitiveDirectory(out _);
        string candidate = UpperRoot + "/escape/outside-canary.txt";

        Assert.False(resolver.IsWithin(UpperRoot, candidate));
        Assert.Equal(LowerSibling + "/outside-canary.txt", resolver.Canonicalize(candidate));
        Assert.Equal(UpperRoot, resolver.Canonicalize(UpperRoot));
        Assert.Equal(LowerSibling, resolver.Canonicalize(LowerSibling));
    }

    [Fact]
    public void TheDeploymentViewRefusesTheSameEscape()
    {
        FileSystemPathResolver resolver = WindowsVolumeWithCaseSensitiveDirectory(out _);
        SquadFileSystemPathSemantics semantics = new(resolver);

        _ = Assert.Throws<SquadPathContainmentException>(
            () => SquadPathPolicy.ResolveFile(UpperRoot, "escape/outside-canary.txt", semantics));
    }

    [Fact]
    public void IsWithinStillFoldsASpellingTheVolumeComparesIgnoringCase()
    {
        FileSystemPathResolver resolver = WindowsVolumeWithCaseSensitiveDirectory(out _);
        string root = UpperRoot + "/folder";
        string candidate = CaseParent.ToUpperInvariant() + "/Root/folder/leaf.md";

        Assert.True(resolver.IsWithin(root, candidate));
        Assert.Equal(UpperRoot + "/folder/leaf.md", resolver.Canonicalize(candidate));
    }

    /// <summary>
    /// Builds the configuration the Windows job runs in: the volume folds names, the parent directory matches them
    /// exactly, and one of the two siblings that differ only in case is the target of a link inside the root.
    /// </summary>
    private static FileSystemPathResolver WindowsVolumeWithCaseSensitiveDirectory(out InMemoryFileSystem files)
    {
        files = new InMemoryFileSystem(caseSensitive: false);
        files.CreateDirectory(CaseParent, caseSensitiveNames: true);
        files.CreateDirectory(UpperRoot);
        files.CreateDirectory(LowerSibling);
        files.CreateDirectory(UpperRoot + "/folder");
        files.WriteAllText(LowerSibling + "/outside-canary.txt", "outside canary");
        files.CreateDirectoryLink(UpperRoot + "/escape", LowerSibling);

        // The host rule of an NTFS volume: names fold to one entry unless the directory says otherwise.
        return new FileSystemPathResolver(files, caseSensitiveFileSystem: false);
    }
}
