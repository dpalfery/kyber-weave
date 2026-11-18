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
    private readonly InMemoryFileSystem _files = new(caseSensitive: false);
    private readonly string _caseParent;
    private readonly string _upperRoot;
    private readonly string _lowerSibling;

    public CaseDistinctSiblingPathTests()
    {
        _caseParent = _files.CreateTemporaryDirectory("kw-case-distinct-");
        _files.CreateDirectory(_caseParent, caseSensitiveNames: true);
        _upperRoot = Path.Combine(_caseParent, "Root");
        _lowerSibling = Path.Combine(_caseParent, "root");
    }

    [Fact]
    public void IsWithinRefusesALinkIntoACaseDistinctSibling()
    {
        FileSystemPathResolver resolver = WindowsVolumeWithCaseSensitiveDirectory();
        string candidate = Path.Combine(_upperRoot, "escape", "outside-canary.txt");

        Assert.False(resolver.IsWithin(_upperRoot, candidate));
        Assert.Equal(Path.Combine(_lowerSibling, "outside-canary.txt"), resolver.Canonicalize(candidate));
        Assert.Equal(_upperRoot, resolver.Canonicalize(_upperRoot));
        Assert.Equal(_lowerSibling, resolver.Canonicalize(_lowerSibling));
    }

    [Fact]
    public void TheDeploymentViewRefusesTheSameEscape()
    {
        FileSystemPathResolver resolver = WindowsVolumeWithCaseSensitiveDirectory();
        SquadFileSystemPathSemantics semantics = new(resolver);

        _ = Assert.Throws<SquadPathContainmentException>(
            () => SquadPathPolicy.ResolveFile(_upperRoot, "escape/outside-canary.txt", semantics));
    }

    [Fact]
    public void IsWithinStillFoldsASpellingTheVolumeComparesIgnoringCase()
    {
        FileSystemPathResolver resolver = WindowsVolumeWithCaseSensitiveDirectory();
        string root = Path.Combine(_upperRoot, "folder");
        string candidate = Path.Combine(
            _caseParent.ToUpperInvariant(),
            Path.GetFileName(_upperRoot),
            "folder",
            "leaf.md");

        Assert.True(resolver.IsWithin(root, candidate));
        Assert.Equal(Path.Combine(_upperRoot, "folder", "leaf.md"), resolver.Canonicalize(candidate));
    }

    /// <summary>
    /// Pins what a project transaction's lease is keyed by: two roots whose names differ only in case hold
    /// distinct keys and so never contend, while a link to a root holds that root's own key and so does contend.
    /// </summary>
    /// <remarks>
    /// <see cref="SquadStateStore.ResolveLeaseIdentity" /> asks the deployment view for exactly this expression, so a
    /// key that folds the two siblings together would either let two deployments write one root at once or refuse a
    /// second one that names the same root through a link. The Windows job's two case-sensitive and link cases run
    /// only there; this one runs everywhere.
    /// </remarks>
    [Fact]
    public void ProjectLeaseKeysSeparateCaseDistinctRootsAndFollowALink()
    {
        _files.CreateDirectory(_upperRoot);
        _files.CreateDirectory(_lowerSibling);
        string alias = Path.Combine(_caseParent, "Root-alias");
        _files.CreateDirectoryLink(alias, _upperRoot);
        SquadFileSystemPathSemantics semantics = new(new FileSystemPathResolver(_files, caseSensitiveFileSystem: false));

        string upperKey = SquadPhysicalRootIdentity.Resolve(_upperRoot, semantics).Key;
        string lowerKey = SquadPhysicalRootIdentity.Resolve(_lowerSibling, semantics).Key;
        string aliasKey = SquadPhysicalRootIdentity.Resolve(alias, semantics).Key;

        Assert.NotEqual(upperKey, lowerKey);
        Assert.Equal(upperKey, aliasKey);
    }

    /// <summary>
    /// Builds the configuration the Windows job runs in: the volume folds names, the parent directory matches them
    /// exactly, and one of the two siblings that differ only in case is the target of a link inside the root.
    /// </summary>
    private FileSystemPathResolver WindowsVolumeWithCaseSensitiveDirectory()
    {
        _files.CreateDirectory(_upperRoot);
        _files.CreateDirectory(_lowerSibling);
        _files.CreateDirectory(Path.Combine(_upperRoot, "folder"));
        _files.WriteAllText(Path.Combine(_lowerSibling, "outside-canary.txt"), "outside canary");
        _files.CreateDirectoryLink(Path.Combine(_upperRoot, "escape"), _lowerSibling);

        // The host rule of an NTFS volume: names fold to one entry unless the directory says otherwise.
        return new FileSystemPathResolver(_files, caseSensitiveFileSystem: false);
    }
}
