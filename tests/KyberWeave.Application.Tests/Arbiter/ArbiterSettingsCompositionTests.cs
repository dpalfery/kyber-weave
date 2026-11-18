using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Arbiter;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The Arbiter's user settings, override writer and model targets reader live in Application now, so the
/// application composition root registers them over the file-system seams: a host that only called the data layer
/// would get nothing to resolve.
/// </summary>
public sealed class ArbiterSettingsCompositionTests
{
    [Fact]
    public void TheApplicationCompositionResolvesTheSettingsServicesOverTheFileSystemSeams()
    {
        using ServiceProvider provider = new ServiceCollection().AddWeaveApplication().BuildServiceProvider();

        _ = Assert.IsType<FileArbiterUserSettings>(provider.GetRequiredService<IArbiterUserSettings>());
        _ = Assert.IsType<FileArbiterUserOverrideWriter>(provider.GetRequiredService<IArbiterUserOverrideWriter>());
        _ = Assert.IsType<FileArbiterModelTargetsReader>(provider.GetRequiredService<IArbiterModelTargetsReader>());
        _ = Assert.IsType<FileTextFacts>(provider.GetRequiredService<IFileTextFacts>());
        _ = Assert.IsType<GateReportFacts>(provider.GetRequiredService<IGateReportFacts>());
    }
}
