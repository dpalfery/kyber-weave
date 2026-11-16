using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Cli.Composition;

/// <summary>Registers what the Arbiter commands take from the host: the console key input.</summary>
internal static class ArbiterCliServices
{
    /// <summary>Registers the host's Arbiter choices with <c>TryAdd</c>, so a test's own registration wins.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddArbiterCliServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IArbiterKeyInput, ConsoleArbiterKeyInput>();
        services.TryAddSingleton<IProcessExecutor>(_ => ProcessExecutor.Instance);
        services.TryAddSingleton(sp => new ArbiterProcessProbe(sp.GetRequiredService<IProcessExecutor>()));
        return services;
    }
}
