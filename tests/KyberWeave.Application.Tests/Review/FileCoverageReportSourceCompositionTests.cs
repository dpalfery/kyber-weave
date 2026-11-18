using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The coverage report source lives in Application now, so the application composition root registers it and the file
/// system it reads through: a host that only called the data layer would get nothing to resolve.
/// </summary>
public sealed class FileCoverageReportSourceCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheCoverageReportSourceOverTheFileSystemSeam()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        _ = Assert.IsType<FileCoverageReportSource>(provider.GetRequiredService<ICoverageReportSource>());
    }
}
