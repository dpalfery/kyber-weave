using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The contained file reader lives in Application now, so the application composition root registers it over the file
/// system seam: a host that only called the data layer would get nothing to resolve.
/// </summary>
public sealed class FileContainedFileReaderCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheContainedFileReaderOverTheFileSystemSeam()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        _ = Assert.IsType<FileContainedFileReader>(provider.GetRequiredService<IContainedFileReader>());
    }
}
