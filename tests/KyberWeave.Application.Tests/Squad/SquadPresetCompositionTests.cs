using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>The application composition builds the preset selection store with every seam it takes.</summary>
public sealed class SquadPresetCompositionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("kw-presetcomp-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void TheSelectionStoreResolvesAndPersistsThroughTheRealFileSystem()
    {
        string home = Path.Combine(_root.FullName, "home");
        string project = Path.Combine(_root.FullName, "project");
        _ = Directory.CreateDirectory(project);
        ServiceCollection services = [];
        services.AddSingleton<ISquadUserPaths>(new TempUserPaths(home, _root.FullName));
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        ISquadModelPresetSelectionStore store = provider.GetRequiredService<ISquadModelPresetSelectionStore>();
        store.Record(project, SquadTarget.Claude, "zai");

        Assert.Single(Directory.GetFiles(Path.Combine(home, ".kyber-weave", "squad", "selections"), "*.yml"));
    }

    private sealed record TempUserPaths(string HomeDirectory, string ApplicationDataDirectory) : ISquadUserPaths;
}
