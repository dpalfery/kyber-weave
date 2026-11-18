using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The tree probe lives in Application now and takes the file-system seam, so the application composition root
/// registers it: this fails if the registration is dropped or the seam is not available to it.
/// </summary>
public sealed class FileRepositoryTreeProbeCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheTreeProbeOverTheFileSystemSeam()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        IRepositoryTreeProbe probe = provider.GetRequiredService<IRepositoryTreeProbe>();

        Assert.IsType<FileRepositoryTreeProbe>(probe);
        Assert.False(probe.ProbeRoot(Path.Combine(AppContext.BaseDirectory, "no-such-root")).Exists);
    }
}
