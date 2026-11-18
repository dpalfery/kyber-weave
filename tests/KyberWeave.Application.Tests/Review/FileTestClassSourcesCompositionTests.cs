using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The test-class source reader lives in Application now and takes the file-system and process-environment seams,
/// so the application composition root registers it: this fails if the registration is dropped or a seam is missing.
/// </summary>
public sealed class FileTestClassSourcesCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheSourceReaderOverTheFileSystemAndEnvironmentSeams()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        ITestClassSources sources = provider.GetRequiredService<ITestClassSources>();

        Assert.IsType<FileTestClassSources>(sources);
        Assert.Null(sources.Open(Path.Combine(AppContext.BaseDirectory, "no-such-root")));
    }
}
