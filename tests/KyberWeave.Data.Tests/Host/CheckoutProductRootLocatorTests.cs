using KyberWeave.Data.Host;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>The checkout locator answers from an in-memory tree: a product folder beside the solution file, or nothing.</summary>
public sealed class CheckoutProductRootLocatorTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly string _root;
    private readonly CheckoutProductRootLocator _locator;

    public CheckoutProductRootLocatorTests()
    {
        _root = Path.Combine(_files.CreateTemporaryDirectory("kw-checkout-"), "checkout");
        _files.CreateDirectory(_root);
        _locator = new CheckoutProductRootLocator(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: _root));
    }

    [Fact]
    public void ADirectoryWithTheSolutionAndTheProductTreeLocatesTheProduct()
    {
        _files.CreateDirectory(Path.Combine(_root, "src", "Product"));
        _files.WriteAllText(Path.Combine(_root, "KyberWeave.sln"), string.Empty);

        string? located = _locator.Locate(_root, "src/Product");

        Assert.True(_files.DirectoryExists(located!));
        Assert.Equal("Product", Path.GetFileName(located));
    }

    [Fact]
    public void AProductPathSpelledWithATrailingSeparatorKeepsIt()
    {
        _files.CreateDirectory(Path.Combine(_root, "src", "Product"));
        _files.WriteAllText(Path.Combine(_root, "KyberWeave.sln"), string.Empty);

        Assert.Equal(Path.Combine(_root, "src", "Product") + Path.DirectorySeparatorChar, _locator.Locate(_root + Path.DirectorySeparatorChar, "src/Product/"));
    }

    [Fact]
    public void AProductPathSpelledWithoutATrailingSeparatorHasNone()
    {
        _files.CreateDirectory(Path.Combine(_root, "src", "Product"));
        _files.WriteAllText(Path.Combine(_root, "KyberWeave.sln"), string.Empty);

        Assert.Equal(Path.Combine(_root, "src", "Product"), _locator.Locate(_root + Path.DirectorySeparatorChar, "src/Product"));
    }

    [Fact]
    public void AMissingSolutionFileLocatesNothing()
    {
        _files.CreateDirectory(Path.Combine(_root, "src", "Product"));

        Assert.Null(_locator.Locate(_root, "src/Product"));
    }

    [Fact]
    public void AMissingProductTreeLocatesNothing()
    {
        _files.CreateDirectory(_root);
        _files.WriteAllText(Path.Combine(_root, "KyberWeave.sln"), string.Empty);

        Assert.Null(_locator.Locate(_root, "src/Product"));
    }

    [Fact]
    public void ADirectoryThatDoesNotExistLocatesNothing() =>
        Assert.Null(_locator.Locate(Path.Combine(_root, "elsewhere"), "src/Product"));

    [Fact]
    public void ABlankWorkingDirectoryLocatesNothing() =>
        Assert.Null(_locator.Locate("  ", "src/Product"));

    [Fact]
    public void ItNeverClimbsToAParentDirectory()
    {
        _files.CreateDirectory(Path.Combine(_root, "src", "Product", "nested"));
        _files.WriteAllText(Path.Combine(_root, "KyberWeave.sln"), string.Empty);

        Assert.Null(_locator.Locate(Path.Combine(_root, "src", "Product", "nested"), "src/Product"));
    }
}
