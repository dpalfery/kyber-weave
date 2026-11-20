using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberDash.Application;

/// <summary>
/// Composition root for the KyberDash application layer.
/// </summary>
/// <remarks>
/// This is the one place in the layer allowed to name <see cref="KyberDash.Data"/>: it delegates
/// repository registration there, then registers the engines and services that sit on top.
/// Every registration uses <c>TryAdd</c>, so a host or test that registers its own implementation
/// before calling this keeps it.
/// </remarks>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data layer and every application engine and service.
    /// </summary>
    /// <param name="services">Service collection to populate.</param>
    /// <param name="options">Composition options; defaults are used when omitted.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddDashApplication(this IServiceCollection services, DashOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        DashOptions resolvedOptions = options ?? new DashOptions();
        services.TryAddSingleton(resolvedOptions);

        _ = Data.ServiceCollectionExtensions.AddDashDataServices(services, resolvedOptions);

        RegisterEngines(services, resolvedOptions);
        RegisterServices(services);

        return services;
    }

    /// <summary>Registers the domain-facing engines and support components.</summary>
    /// <param name="services">Service collection to populate.</param>
    /// <param name="options">Composition options.</param>
    private static void RegisterEngines(IServiceCollection services, DashOptions options)
    {
        services.TryAddSingleton(provider =>
        {
            IPricingRepository pricing = provider.GetRequiredService<IPricingRepository>();
            _ = pricing.LoadPricingTables();
            return pricing;
        });
        services.TryAddSingleton(provider => new CanonSessionProjectorEngine(
            provider.GetRequiredService<IRelationalStoreReader>(),
            provider.GetRequiredService<IProjectionStore>(),
            provider.GetRequiredService<IOperationalStore>(),
            provider.GetRequiredService<IPricingRepository>()));

        services.TryAddSingleton(provider => new CanonRollupsEngine(
            provider.GetRequiredService<IRelationalStoreReader>(),
            provider.GetRequiredService<IProjectionStore>(),
            provider.GetRequiredService<IPricingRepository>()));

        services.TryAddSingleton(provider => new CanonRefreshEngine(
            provider.GetRequiredService<IRelationalStoreWriter>(),
            provider.GetRequiredService<IRelationalStoreReader>(),
            provider.GetRequiredService<IOperationalStore>(),
            provider.GetRequiredService<HarnessSourceRegistry>(),
            [.. provider.GetServices<IHarnessMapper>()],
            provider.GetService<IHarnessFileSystem>(),
            provider.GetRequiredService<CanonSessionProjectorEngine>(),
            provider.GetRequiredService<CanonRollupsEngine>(),
            provider.GetService<DashOptions>()));

        services.TryAddSingleton(provider => new KyberBridgeEngine(
            provider.GetRequiredService<IRelationalStoreReader>(),
            provider.GetRequiredService<IProjectionStore>(),
            provider.GetRequiredService<IOperationalStore>(),
            provider.GetRequiredService<IPricingRepository>(),
            provider.GetRequiredService<CanonSessionProjectorEngine>(),
            provider.GetRequiredService<CanonRollupsEngine>(),
            string.IsNullOrWhiteSpace(options.Version) ? KyberBridgeEngine.DefaultVersion : options.Version));
    }

    /// <summary>Registers every <c>KyberDash.Contracts.Services</c> implementation.</summary>
    /// <param name="services">Service collection to populate.</param>
    private static void RegisterServices(IServiceCollection services)
    {
        services.TryAddSingleton<CanonProjectionService>(provider => new CanonProjectionService(
            provider.GetRequiredService<IRelationalStoreReader>(),
            provider.GetRequiredService<IProjectionStore>(),
            provider.GetRequiredService<IOperationalStore>(),
            provider.GetRequiredService<CanonSessionProjectorEngine>(),
            provider.GetRequiredService<CanonRollupsEngine>()));
        services.TryAddSingleton<ICanonProjectionService>(provider => provider.GetRequiredService<CanonProjectionService>());

        services.TryAddSingleton<IHarnessFileSystem>(_ => PhysicalHarnessFileSystem.Instance);
        services.TryAddSingleton<HarnessSourceRegistry>(_ => new HarnessSourceRegistry());

        services.TryAddSingleton<TranscriptIngestionService>(provider => new TranscriptIngestionService(
            provider.GetRequiredService<IRelationalStoreWriter>(),
            provider.GetRequiredService<IOperationalStore>(),
            provider.GetRequiredService<HarnessSourceRegistry>(),
            [.. provider.GetServices<IHarnessMapper>()],
            provider.GetService<IHarnessFileSystem>(),
            provider.GetService<DashOptions>()));
        services.TryAddSingleton<ITranscriptIngestionService>(provider => provider.GetRequiredService<TranscriptIngestionService>());

        services.TryAddSingleton<IOtlpCollectorService>(provider => new OtlpCollectorService(
            provider.GetRequiredService<IRelationalStoreWriter>(),
            provider.GetRequiredService<IOperationalStore>(),
            provider.GetRequiredService<ITranscriptIngestionService>(),
            provider.GetRequiredService<DashOptions>()));

        services.TryAddSingleton<IDashboardQueryService>(provider =>
            new DashboardQueryService(provider.GetRequiredService<KyberBridgeEngine>()));

        services.TryAddSingleton<IAnalyticsService>(provider =>
            new AnalyticsService(provider.GetRequiredService<KyberBridgeEngine>()));

        services.TryAddSingleton<ICleanService>(provider => new CleanService(
            provider.GetRequiredService<ICanonStoreRepository>(),
            provider.GetRequiredService<ICanonProjectionService>(),
            provider.GetRequiredService<ITranscriptIngestionService>()));

        services.TryAddSingleton<ISettingsService>(provider =>
            new SettingsService(provider.GetRequiredService<ISettingsRepository>()));

        services.TryAddSingleton<ICaptureService>(provider => new CaptureService(
            provider.GetRequiredService<ISettingsRepository>(),
            provider.GetRequiredService<ICanonStoreRepository>(),
            provider.GetRequiredService<IServerStateRepository>()));

        services.TryAddSingleton<IJobSchedulerService>(provider => new JobSchedulerService(
            provider.GetRequiredService<ICanonStoreRepository>(),
            provider.GetRequiredService<ICanonProjectionService>(),
            provider.GetRequiredService<ITranscriptIngestionService>(),
            provider.GetRequiredService<ISettingsRepository>(),
            provider.GetRequiredService<IServerStateRepository>(),
            provider.GetRequiredService<DashOptions>()));

        services.TryAddSingleton<IDoctorService>(provider => new DoctorService(
            provider.GetRequiredService<ICanonStoreRepository>(),
            provider.GetRequiredService<KyberDash.Core.Resilience.ICircuitBreaker>()));

        services.TryAddSingleton<IReportService>(provider => new ReportService(
            provider.GetRequiredService<IDashboardQueryService>(),
            provider.GetRequiredService<ICanonStoreRepository>()));

        services.TryAddSingleton<IMenubarService>(provider =>
            new MenubarService(provider.GetRequiredService<IServerStateRepository>()));

        services.TryAddSingleton<IEngineLifecycleService>(provider => new EngineLifecycleService(
            provider.GetRequiredService<IServerStateRepository>(),
            provider.GetRequiredService<DashOptions>()));

        services.TryAddSingleton<IE2eSeederService>(provider => new E2eSeederService(
            provider.GetRequiredService<ITranscriptIngestionService>(),
            provider.GetRequiredService<ICanonProjectionService>(),
            provider.GetRequiredService<ICanonStoreRepository>()));

        services.TryAddSingleton<IWebAssetService>(provider => new WebAssetService(
            provider.GetRequiredService<IWebAssetRepository>(),
            provider.GetRequiredService<DashOptions>()));
    }
}
