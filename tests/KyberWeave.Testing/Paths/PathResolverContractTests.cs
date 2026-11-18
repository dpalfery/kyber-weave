using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Tests.Paths;

/// <summary>
/// The contract every <see cref="IPathResolver"/> must satisfy. Run it once against the resolver over the in-memory
/// file system and once against the resolver over the real disk, so a test that pins a case rule or an alias the
/// machine does not have still proves the adapter agrees.
/// </summary>
/// <remarks>
/// A derived class supplies the file system and the resolver built over it, plus the case rule that file system
/// has. The suite creates its own scratch directories through that file system, so it works for a fake and for the
/// real disk and leaves nothing behind.
/// </remarks>
public abstract class PathResolverContractTests : IDisposable
{
    private readonly List<(IWritableFileSystem Files, string Path)> _scratch = [];

    /// <summary>Creates the file system the resolver answers from.</summary>
    /// <returns>A writable file system, with the case rule <see cref="CaseSensitive" /> reports.</returns>
    protected abstract IWritableFileSystem CreateFileSystem();

    /// <summary>Wraps the file system in the resolver under test.</summary>
    /// <param name="files">The file system from <see cref="CreateFileSystem" />.</param>
    /// <returns>The resolver under test.</returns>
    protected abstract IPathResolver CreateResolver(IFileSystem files);

    /// <summary>Gets whether the file system compares names case-sensitively.</summary>
    protected abstract bool CaseSensitive { get; }

    /// <summary>Creates the resolver and one empty scratch directory.</summary>
    /// <param name="files">The file system under test.</param>
    /// <param name="resolver">The resolver under test.</param>
    /// <param name="root">The scratch directory the test works in.</param>
    protected void Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root)
    {
        files = CreateFileSystem();
        resolver = CreateResolver(files);
        root = NewScratch(files, "kw-path-contract-");
    }

    /// <summary>Creates a second empty scratch directory, outside the tree the test asserts containment for.</summary>
    /// <param name="files">The file system under test.</param>
    /// <param name="prefix">The directory-name prefix.</param>
    /// <returns>The new directory.</returns>
    protected string AnotherDirectory(IWritableFileSystem files, string prefix) => NewScratch(files, prefix);

    /// <summary>Removes every scratch directory the test created.</summary>
    public void Dispose()
    {
        foreach ((IWritableFileSystem files, string path) in _scratch)
        {
            try
            {
                files.DeleteTreeNoFollow(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover sits under the system temporary directory.
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AFullPathIsResolvedAgainstTheExplicitBase()
    {
        Starting(out _, out IPathResolver resolver, out string root);

        Assert.Equal(Path.Combine(root, "a", "b.txt"), resolver.GetFullPath("a/b.txt", root));
        Assert.Equal(root, resolver.GetFullPath(".", root));
    }

    [Fact]
    public void AFullPathIgnoresTheBaseWhenThePathIsAlreadyAbsolute()
    {
        Starting(out _, out IPathResolver resolver, out string root);
        string elsewhere = Path.Combine(root, "elsewhere", "x.txt");

        Assert.Equal(elsewhere, resolver.GetFullPath(elsewhere, Path.Combine(root, "ignored")));
    }

    [Fact]
    public void AFullPathResolvesDotAndDotDotLexically()
    {
        Starting(out _, out IPathResolver resolver, out string root);

        Assert.Equal(Path.Combine(root, "c.txt"), resolver.GetFullPath("sub/../c.txt", root));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(root, "..", "outside.txt")),
            resolver.GetFullPath("../outside.txt", root));
    }

    [Fact]
    public void AFullPathRefusesABaseThatIsNotFullyQualified()
    {
        Starting(out _, out IPathResolver resolver, out string root);

        _ = Assert.Throws<ArgumentException>(() => resolver.GetFullPath("a", "relative/base"));
        _ = Assert.Throws<ArgumentException>(() => resolver.GetFullPath("a", " "));
        _ = Assert.Throws<ArgumentException>(() => resolver.GetFullPath(" ", root));
        _ = Assert.Throws<ArgumentException>(() => resolver.GetFullPath(string.Empty, root));
    }

    [Fact]
    public void AFullPathNeverReadsTheProcessWorkingDirectory()
    {
        Starting(out _, out IPathResolver resolver, out string root);

        string resolved = resolver.GetFullPath("only-a-relative-name.txt", root);

        Assert.StartsWith(root + Path.DirectorySeparatorChar, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullPathTrimsATrailingSeparatorButKeepsARoot()
    {
        Starting(out _, out IPathResolver resolver, out string root);
        string filesystemRoot = Path.GetPathRoot(root)!;

        Assert.Equal(root, resolver.GetFullPath(root + Path.DirectorySeparatorChar, root));
        Assert.Equal(filesystemRoot, resolver.GetFullPath(filesystemRoot, root));
    }

    [Fact]
    public void AreSameComparesSpellingsByTheFileSystemsCaseRule()
    {
        Starting(out _, out IPathResolver resolver, out string root);
        string name = Path.Combine(root, "Name.txt");
        string otherCase = Path.Combine(root, "nAME.txt");

        Assert.True(resolver.AreSame(name, name));
        Assert.Equal(!CaseSensitive, resolver.AreSame(name, otherCase));
        Assert.False(resolver.AreSame(name, Path.Combine(root, "Other.txt")));
    }

    [Fact]
    public void AreSameIgnoresATrailingSeparatorAndRefusesARelativePath()
    {
        Starting(out _, out IPathResolver resolver, out string root);

        Assert.True(resolver.AreSame(root, root + Path.DirectorySeparatorChar));
        _ = Assert.Throws<ArgumentException>(() => resolver.AreSame(root, "relative"));
        _ = Assert.Throws<ArgumentException>(() => resolver.AreSame("relative", root));
    }

    [Fact]
    public void IsWithinContainsAChildAndRefusesASiblingTheParentAndItself()
    {
        Starting(out _, out IPathResolver resolver, out string root);
        string child = Path.Combine(root, "folder", "leaf.md");

        Assert.True(resolver.IsWithin(root, child));
        Assert.True(resolver.IsWithin(root, Path.Combine(root, "folder")));
        Assert.False(resolver.IsWithin(root, Path.Combine(root, "..", "sibling", "leaf.md")));
        Assert.False(resolver.IsWithin(child, root));
        Assert.False(resolver.IsWithin(root, root));
    }

    [Fact]
    public void IsWithinTreatsTheFilesystemRootAsTheOutermostDirectory()
    {
        Starting(out _, out IPathResolver resolver, out string root);
        string filesystemRoot = Path.GetPathRoot(root)!;

        Assert.True(resolver.IsWithin(filesystemRoot, root));
        Assert.False(resolver.IsWithin(root, filesystemRoot));
        Assert.False(resolver.IsWithin(filesystemRoot, filesystemRoot));
    }

    [Fact]
    public void IsWithinRefusesARelativePath()
    {
        Starting(out _, out IPathResolver resolver, out string root);

        _ = Assert.Throws<ArgumentException>(() => resolver.IsWithin(root, "relative"));
        _ = Assert.Throws<ArgumentException>(() => resolver.IsWithin("relative", root));
    }

    [Fact]
    public void IsWithinFollowsALinkAndRefusesThePathItCarriesOutOfTheRoot()
    {
        Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root);
        string outside = AnotherDirectory(files, "kw-path-outside-");
        string insideReal = Path.Combine(root, "real");
        files.CreateDirectory(insideReal);
        files.WriteAllText(Path.Combine(insideReal, "leaf.md"), "leaf");
        files.CreateDirectory(outside);
        files.WriteAllText(Path.Combine(outside, "leaf.md"), "outside");
        string insideLink = Path.Combine(root, "inside-link");
        string outsideLink = Path.Combine(root, "outside-link");
        files.CreateDirectoryLink(insideLink, insideReal);
        files.CreateDirectoryLink(outsideLink, outside);

        Assert.True(resolver.IsWithin(root, Path.Combine(insideLink, "leaf.md")));
        Assert.False(resolver.IsWithin(root, Path.Combine(outsideLink, "leaf.md")));
        Assert.Equal(
            Path.Combine(resolver.Canonicalize(insideReal), "leaf.md"),
            resolver.Canonicalize(Path.Combine(insideLink, "leaf.md")));
    }

    [Fact]
    public void CanonicalizeUsesTheOnDiskSpellingOfEveryExistingSegment()
    {
        Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root);
        string onDiskMiddle = Path.Combine(root, "MiddleSegment");
        files.CreateDirectory(onDiskMiddle);
        files.WriteAllText(Path.Combine(onDiskMiddle, "Leaf.md"), "leaf");
        string canonicalRoot = resolver.Canonicalize(root);

        string canonical = resolver.Canonicalize(Path.Combine(root, "middlesegment", "leaf.md"));

        string expectedLeaf = CaseSensitive ? "leaf.md" : "Leaf.md";
        string expectedMiddle = CaseSensitive ? "middlesegment" : "MiddleSegment";
        Assert.Equal(Path.Combine(canonicalRoot, expectedMiddle, expectedLeaf), canonical);
    }

    [Fact]
    public void CanonicalizeKeepsANotYetExistingTailInItsGivenSpelling()
    {
        Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root);
        string existing = Path.Combine(root, "Existing");
        files.CreateDirectory(existing);

        string canonical = resolver.Canonicalize(Path.Combine(existing, "new", "deeper", "leaf.md"));

        Assert.Equal(
            Path.Combine(resolver.Canonicalize(existing), "new", "deeper", "leaf.md"),
            canonical);
    }

    [Fact]
    public void CanonicalizeReturnsTheFilesystemRootAsItselfAndRefusesARelativePath()
    {
        Starting(out _, out IPathResolver resolver, out string root);
        string filesystemRoot = Path.GetPathRoot(root)!;

        Assert.Equal(filesystemRoot, resolver.Canonicalize(filesystemRoot));
        _ = Assert.Throws<ArgumentException>(() => resolver.Canonicalize("relative/leaf.md"));
    }

    [Fact]
    public void CanonicalizeNormalizesTheSpellingToUnicodeFormC()
    {
        Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root);
        const string decomposed = "e\u0301.md";
        const string composed = "\u00e9.md";
        files.WriteAllText(Path.Combine(root, decomposed), "x");

        string canonical = resolver.Canonicalize(Path.Combine(root, decomposed));

        Assert.Equal(Path.Combine(resolver.Canonicalize(root), composed), canonical);
    }

    [Fact]
    public void CanonicalizeKeepsTheLinkSpellingWhenLinksFormACycle()
    {
        Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root);
        string first = Path.Combine(root, "loop-a");
        string second = Path.Combine(root, "loop-b");
        files.CreateDirectoryLink(first, second);
        files.CreateDirectoryLink(second, first);

        string canonical = resolver.Canonicalize(first);

        Assert.Equal(Path.Combine(resolver.Canonicalize(root), "loop-a"), canonical);
    }

    [Fact]
    public void ResolvePhysicalRootReturnsTheCanonicalSpellingOfAnExistingDirectory()
    {
        Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root);
        string real = Path.Combine(root, "real");
        files.CreateDirectory(real);
        string link = Path.Combine(root, "link");
        files.CreateDirectoryLink(link, real);

        Assert.Equal(resolver.Canonicalize(real), resolver.ResolvePhysicalRoot(real));
        Assert.Equal(resolver.Canonicalize(real), resolver.ResolvePhysicalRoot(link));
    }

    [Fact]
    public void ResolvePhysicalRootRefusesAMissingDirectoryAFileAndARelativePath()
    {
        Starting(out IWritableFileSystem files, out IPathResolver resolver, out string root);
        files.WriteAllText(Path.Combine(root, "leaf.md"), "leaf");

        _ = Assert.Throws<DirectoryNotFoundException>(
            () => resolver.ResolvePhysicalRoot(Path.Combine(root, "missing")));
        _ = Assert.Throws<DirectoryNotFoundException>(
            () => resolver.ResolvePhysicalRoot(Path.Combine(root, "leaf.md")));
        _ = Assert.Throws<ArgumentException>(() => resolver.ResolvePhysicalRoot("relative"));
    }

    private string NewScratch(IWritableFileSystem files, string prefix)
    {
        string path = files.CreateTemporaryDirectory(prefix);
        _scratch.Add((files, path));
        return path;
    }
}
