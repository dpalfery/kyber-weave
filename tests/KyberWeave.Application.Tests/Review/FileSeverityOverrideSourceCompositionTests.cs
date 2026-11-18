using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The severity override source lives in Application now, so the application composition root registers it over the
/// file seam: a host that only called the data layer would get nothing to resolve.
/// </summary>
public sealed class FileSeverityOverrideSourceCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheSeverityOverrideSourceOverTheFileSeam()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        _ = Assert.IsType<FileSeverityOverrideSource>(provider.GetRequiredService<ISeverityOverrideSource>());
    }
}
