using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The file lister lives in Application now and takes the process and file-system seams, so the application
/// composition root registers it: this fails if the registration is dropped or either seam is not available to it.
/// </summary>
public sealed class FileRepositoryFileListerCompositionTests
{
    [Fact]
    public async Task TheApplicationCompositionResolvesTheFileListerOverTheProcessAndFileSystemSeams()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        IRepositoryFileLister lister = provider.GetRequiredService<IRepositoryFileLister>();

        Assert.IsType<FileRepositoryFileLister>(lister);
        _ = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => lister.ListAsync(Path.Combine(AppContext.BaseDirectory, "no-such-root"), CancellationToken.None));
    }
}
