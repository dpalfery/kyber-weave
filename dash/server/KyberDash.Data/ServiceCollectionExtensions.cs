using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberDash.Data;

/// <summary>Composition of the KyberDash data-access layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data-access repositories. Every registration uses <c>TryAdd</c>, so a host or a test
    /// that registers its own implementation before calling this method keeps it.
    /// </summary>
    /// <param name="services">Service collection to populate.</param>
    /// <param name="options">Composition options; defaults are used when omitted.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddDashDataServices(this IServiceCollection services, DashOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        DashOptions resolved = options ?? new DashOptions();
        services.TryAddSingleton(resolved);

        services.TryAddSingleton<ICircuitBreaker>(provider =>
            new SqliteCircuitBreaker(
                provider.GetService<DashOptions>()?.CircuitBreaker ?? resolved.CircuitBreaker));

        services.TryAddSingleton<SqliteRelationalStore>(provider =>
        {
            DashOptions? current = provider.GetService<DashOptions>();
            string databasePath = CanonDbPath.Resolve(current?.DatabasePath, null, null);
            return new SqliteRelationalStore(databasePath, provider.GetRequiredService<ICircuitBreaker>());
        });

        services.TryAddSingleton<IRelationalStoreWriter>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<IRelationalStoreReader>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<IProjectionStore>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<IOperationalStore>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<ICanonStoreRepository>(provider =>
        {
            DashOptions? current = provider.GetService<DashOptions>();
            string databasePath = CanonDbPath.Resolve(current?.DatabasePath, null, null);
            return new CanonStoreRepository(databasePath, provider.GetRequiredService<ICircuitBreaker>());
        });

        services.TryAddSingleton<ITranscriptReaderRepository>(_ => new TranscriptReaderRepository());

        services.TryAddSingleton<IPricingRepository>(provider =>
            new PricingRepository(provider.GetRequiredService<IOperationalStore>()));

        services.TryAddSingleton<ISettingsRepository>(provider =>
            new SettingsRepository(provider.GetRequiredService<IOperationalStore>()));

        services.TryAddSingleton<IServerStateRepository>(provider =>
            new ServerStateRepository(provider.GetService<DashOptions>()?.StateDirectory));

        services.TryAddSingleton<IWebAssetRepository>(provider =>
            new WebAssetRepository(provider.GetService<DashOptions>()?.DashboardDir));

        return services;
    }
}
