using KyberWeave.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Mcp;

/// <summary>The application composition builds the two git readers with every seam they now take.</summary>
public sealed class DocsGitCompositionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("kw-gitcomp-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void TheCheckoutProbeResolvesAndReadsARealCheckoutThroughTheRealFileSystem()
    {
        _ = Directory.CreateDirectory(Path.Combine(_root.FullName, ".git"));
        File.WriteAllText(Path.Combine(_root.FullName, ".git", "HEAD"), "ref: refs/heads/composed\n");
        ServiceCollection services = [];
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        IGitCheckoutProbe probe = provider.GetRequiredService<IGitCheckoutProbe>();

        Assert.Equal("composed", probe.ReadLocation(_root.FullName).Branch);
        Assert.NotEqual(0, probe.Fingerprint(_root.FullName));
    }

    [Fact]
    public async Task TheWorktreeIdentityResolvesAndRunsGitThroughTheRealProcessRunner()
    {
        ServiceCollection services = [];
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        IGitWorktreeIdentity identity = provider.GetRequiredService<IGitWorktreeIdentity>();

        GitWorktreeRoots roots = await identity.TryRootsAsync(_root.FullName);
        Assert.True(roots.Found ? roots.TopLevel is not null : roots.Failure is not null);
    }
}
