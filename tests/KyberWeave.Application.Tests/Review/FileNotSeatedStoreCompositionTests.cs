using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The not-seated store lives in Application now and takes the file-system seam, so the application composition root
/// registers it: this fails if the registration is dropped or the seam is not available to it.
/// </summary>
public sealed class FileNotSeatedStoreCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheNotSeatedStoreOverTheFileSystemSeam()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        INotSeatedStore store = provider.GetRequiredService<INotSeatedStore>();

        Assert.IsType<FileNotSeatedStore>(store);
        Assert.Null(store.LatestCycle(Path.Combine(AppContext.BaseDirectory, "no-such-reviews")));
    }
}
