using KyberWeave.Data.Paths;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Paths;

/// <summary>
/// Pins how containment folds the spelling of a path that does not exist yet.
/// </summary>
/// <remarks>
/// A volume compares names by its own rule, so on the volumes that fold names two spellings of a directory that is
/// not there yet are one path, and a deployment that plans a new directory must stay contained when it spells one of
/// those names in another case. The distinction that keeps the case-distinct sibling out is that a sibling is an
/// entry: folding is only sound below the deepest directory that exists.
/// </remarks>
public sealed class PathCaseFoldingTests
{
    private const string ExistingParent = "/not-created-yet";
    private const string CaseSensitiveParent = "/case-distinct-parent";
    private const string UpperRoot = CaseSensitiveParent + "/Root";

    [Fact]
    public void IsWithinFoldsASpellingOfAPathThatDoesNotExistYet()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        files.CreateDirectory(ExistingParent);
        FileSystemPathResolver resolver = new(files, caseSensitiveFileSystem: false);

        Assert.True(resolver.IsWithin(ExistingParent + "/root", ExistingParent + "/ROOT/leaf.md"));
        Assert.True(resolver.IsWithin("/absent", "/ABSENT/leaf.md"));
    }

    [Fact]
    public void IsWithinRefusesACaseVariantOfAPathThatDoesNotExistYetOnACaseSensitiveVolume()
    {
        InMemoryFileSystem files = new(caseSensitive: true);
        files.CreateDirectory(ExistingParent);
        FileSystemPathResolver resolver = new(files, caseSensitiveFileSystem: true);

        Assert.False(resolver.IsWithin(ExistingParent + "/root", ExistingParent + "/ROOT/leaf.md"));
        Assert.False(resolver.IsWithin("/absent", "/ABSENT/leaf.md"));
    }

    [Fact]
    public void IsWithinRefusesACaseVariantOfTheRootsOwnNameEvenWhenTheOtherSpellingIsNotThereYet()
    {
        InMemoryFileSystem files = new(caseSensitive: false);
        files.CreateDirectory(CaseSensitiveParent, caseSensitiveNames: true);
        files.CreateDirectory(UpperRoot);
        FileSystemPathResolver resolver = new(files, caseSensitiveFileSystem: false);

        Assert.False(resolver.IsWithin(UpperRoot, CaseSensitiveParent + "/root/leaf.md"));
    }
}
