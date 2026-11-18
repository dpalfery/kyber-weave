using KyberWeave.Data.Host;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>The checkout locator answers from an in-memory tree: a product folder beside the solution file, or nothing.</summary>
public sealed class CheckoutProductRootLocatorTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "checkout"));

    private readonly InMemoryFileSystem _files = new();
    private readonly CheckoutProductRootLocator _locator;

    public CheckoutProductRootLocatorTests() =>
        _locator = new CheckoutProductRootLocator(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Root));

    [Fact]
    public void ADirectoryWithTheSolutionAndTheProductTreeLocatesTheProduct()
    {
        _files.CreateDirectory(Path.Combine(Root, "src", "Product"));
        _files.WriteAllText(Path.Combine(Root, "KyberWeave.sln"), string.Empty);

        string? located = _locator.Locate(Root, "src/Product");

        Assert.True(_files.DirectoryExists(located!));
        Assert.Equal("Product", Path.GetFileName(located));
    }

    [Fact]
    public void AMissingSolutionFileLocatesNothing()
    {
        _files.CreateDirectory(Path.Combine(Root, "src", "Product"));

        Assert.Null(_locator.Locate(Root, "src/Product"));
    }

    [Fact]
    public void AMissingProductTreeLocatesNothing()
    {
        _files.CreateDirectory(Root);
        _files.WriteAllText(Path.Combine(Root, "KyberWeave.sln"), string.Empty);

        Assert.Null(_locator.Locate(Root, "src/Product"));
    }

    [Fact]
    public void ADirectoryThatDoesNotExistLocatesNothing() =>
        Assert.Null(_locator.Locate(Path.Combine(Root, "elsewhere"), "src/Product"));

    [Fact]
    public void ABlankWorkingDirectoryLocatesNothing() =>
        Assert.Null(_locator.Locate("  ", "src/Product"));

    [Fact]
    public void ItNeverClimbsToAParentDirectory()
    {
        _files.CreateDirectory(Path.Combine(Root, "src", "Product", "nested"));
        _files.WriteAllText(Path.Combine(Root, "KyberWeave.sln"), string.Empty);

        Assert.Null(_locator.Locate(Path.Combine(Root, "src", "Product", "nested"), "src/Product"));
    }
}
