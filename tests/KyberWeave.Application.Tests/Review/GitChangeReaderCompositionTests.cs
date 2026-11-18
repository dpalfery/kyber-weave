using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The git change reader lives in Application now, so the application composition root registers it: a host that only
/// called the data layer would get nothing to resolve.
/// </summary>
public sealed class GitChangeReaderCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheGitChangeReaderOverTheProcessSeam()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        _ = Assert.IsType<GitChangeReader>(provider.GetRequiredService<IGitChangeReader>());
    }
}
