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
    public void TheWorktreeIdentityResolvesAndRunsGitThroughTheRealProcessRunner()
    {
        ServiceCollection services = [];
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        IGitWorktreeIdentity identity = provider.GetRequiredService<IGitWorktreeIdentity>();

        bool named = identity.TryRoots(_root.FullName, out string? top, out _, out string? failure);
        Assert.True(named ? top is not null : failure is not null);
    }
}
