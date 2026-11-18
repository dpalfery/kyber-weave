using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The cited source files live in Application now, so the application composition root registers them over the file
/// system and contained-file seams: a host that only called the data layer would get nothing to resolve.
/// </summary>
public sealed class FileCitedSourceFilesCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheCitedSourceFilesOverItsSeams()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        _ = Assert.IsType<FileCitedSourceFiles>(provider.GetRequiredService<ICitedSourceFiles>());
    }
}
