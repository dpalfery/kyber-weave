using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Cli.Composition;

/// <summary>Registers what the Squad commands take from the host: where releases come from, the renderers and the console.</summary>
internal static class SquadCliServices
{
    /// <summary>Registers the host's Squad choices with <c>TryAdd</c>, so a test's own registration wins.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddSquadCliServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISquadReleaseSource>(sp => new GitHubSquadReleaseSource(
            ReleaseOrigin.Resolve(sp.GetRequiredService<IProcessEnvironment>().GetVariable).ApiRoot));
        services.TryAddSingleton<ISquadInstanceReleaseSource>(sp => new InstanceReleaseSource(
            FindLocalProductSquadRoot(sp.GetRequiredService<ISquadFileAccess>()) is { } local
                ? new LocalCheckoutSquadReleaseSource(local)
                : sp.GetRequiredService<ISquadReleaseSource>()));
        services.TryAddSingleton<ISquadRenderer>(_ => new SquadRendererRegistry(SquadRendererSet.Create()));
        services.TryAddSingleton(new SquadConsoleOptions());
        services.TryAddSingleton(new SquadRunOptions());
        services.TryAddSingleton(new SquadDoctorOptions());
        services.TryAddSingleton<ISquadProjectPicker, SpectreSquadProjectPicker>();
        services.TryAddSingleton(_ => KyberDashCapturePluginStep.CreateDefault());
        return services;
    }

    private static string? FindLocalProductSquadRoot(ISquadFileAccess files)
    {
        try
        {
            return files.FindCheckoutSquadSourceRoot();
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return null;
        }
    }

    /// <summary>The instance service's release source: a local checkout when one is found, otherwise the download.</summary>
    private sealed class InstanceReleaseSource(ISquadReleaseSource inner) : ISquadInstanceReleaseSource
    {
        public Task<SquadReleaseResult> DownloadAndExtractAsync(SquadReleaseRequest request, CancellationToken cancellationToken) =>
            inner.DownloadAndExtractAsync(request, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}
