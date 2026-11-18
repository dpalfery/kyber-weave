using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The git change reader lives in Application now, so the application composition root registers it: this fails if
/// the registration is dropped, which is what a reader left unregistered would surface as only at run time.
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
