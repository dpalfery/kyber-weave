using KyberWeave.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Data.Arbiter;

/// <summary>Registers the Arbiter's adapters.</summary>
internal static class ArbiterDataServiceCollectionExtensions
{
    /// <summary>Registers the Arbiter adapters with <c>TryAdd</c>, so a host or test that registered its own first keeps it.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddWeaveArbiterData(this IServiceCollection services)
    {
        services.TryAddSingleton<IArbiterModelTargetsReader>(sp => new FileArbiterModelTargetsReader(sp.GetRequiredService<IFileSystem>()));
        return services;
    }
}
