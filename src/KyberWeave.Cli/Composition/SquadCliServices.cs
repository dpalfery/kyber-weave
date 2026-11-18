using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
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
        services.TryAddSingleton<ISquadRenderer>(sp => new SquadRendererRegistry(
            SquadRendererSet.Create(),
            sp.GetRequiredService<ISquadRenderInputReader>()));
        services.TryAddSingleton<Func<SelfUpdateHost>>(sp =>
        {
            IProcessEnvironment environment = sp.GetRequiredService<IProcessEnvironment>();
            return () => SelfUpdateHostFactory.FromCurrentProcess(environment);
        });
        services.TryAddSingleton(new SquadConsoleOptions());
        services.TryAddSingleton(new SquadRunOptions());
        services.TryAddSingleton(new SquadDoctorOptions());
        services.TryAddSingleton<ISquadProjectPicker, SpectreSquadProjectPicker>();
        services.TryAddSingleton(_ => KyberDashCapturePluginStep.CreateDefault());
        return services;
    }
}
