using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Antigravity;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Harnesses.Cline;
using KyberDash.Data.Harnesses.Codex;
using KyberDash.Data.Harnesses.CommandCode;
using KyberDash.Data.Harnesses.CopilotCli;
using KyberDash.Data.Harnesses.CopilotVscode;
using KyberDash.Data.Harnesses.Cursor;
using KyberDash.Data.Harnesses.Devin;
using KyberDash.Data.Harnesses.Droid;
using KyberDash.Data.Harnesses.Kilo;
using KyberDash.Data.Harnesses.KimiCode;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Data.Harnesses.Pi;
using KyberDash.Data.Harnesses.RooCode;
using KyberDash.Data.Harnesses.Warp;
using KyberDash.Data.Repositories;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberDash.Data;

/// <summary>Composition of the KyberDash data-access layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data-access repositories and every harness mapper. Every registration uses
    /// <c>TryAdd</c>, so a host or a test that registers its own implementation before calling
    /// this method keeps it.
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

        services.TryAddSingleton<IRelationalStoreFileSystem>(_ => PhysicalRelationalStoreFileSystem.Instance);

        services.TryAddSingleton<SqliteRelationalStore>(provider =>
        {
            DashOptions? current = provider.GetService<DashOptions>();
            string databasePath = CanonDbPath.Resolve(current?.DatabasePath, null, null);
            return new SqliteRelationalStore(
                databasePath,
                provider.GetRequiredService<ICircuitBreaker>(),
                provider.GetRequiredService<IRelationalStoreFileSystem>());
        });

        services.TryAddSingleton<IRelationalStoreWriter>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<IRelationalStoreReader>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<IProjectionStore>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<IOperationalStore>(provider =>
            provider.GetRequiredService<SqliteRelationalStore>());

        services.TryAddSingleton<ITranscriptReaderRepository>(provider => new TranscriptReaderRepository(
            null,
            null,
            ServerStateRepository.ResolveStateDirectory(provider.GetService<DashOptions>()?.StateDirectory)));

        services.TryAddSingleton<IPricingRepository>(provider =>
            new PricingRepository(provider.GetRequiredService<IOperationalStore>()));

        services.TryAddSingleton<ISettingsRepository>(provider =>
            new SettingsRepository(provider.GetRequiredService<IOperationalStore>()));

        services.TryAddSingleton<IServerStateRepository>(provider =>
            new ServerStateRepository(provider.GetService<DashOptions>()?.StateDirectory));

        services.TryAddSingleton<IWebAssetRepository>(provider =>
            new WebAssetRepository(provider.GetService<DashOptions>()?.DashboardDir));

        services.TryAddSingleton<ITextTokenizer>(_ => LocalTextTokenizer.Shared);

        RegisterHarnessMappers(services);

        return services;
    }

    /// <summary>
    /// Registers every <see cref="IHarnessMapper"/> so the engines resolve mapper coverage for each
    /// evidenced harness through the contract. Each registration uses <c>TryAddEnumerable</c>, so a
    /// host that registers its own mapper for a harness keeps it alongside the built-ins.
    /// </summary>
    /// <remarks>
    /// The engines build their mapper lookup keyed on <see cref="IHarnessMapper.HarnessId"/>,
    /// letting later registrations overwrite earlier ones, so the OTLP span mappers register
    /// first and the local transcript mappers last: the ids the two families share (claude-code,
    /// codex-cli, antigravity, droid, pi) must stay owned by the transcript mappers, or a
    /// transcript unit reaching a span mapper would map to nothing.
    /// </remarks>
    /// <param name="services">Service collection to populate.</param>
    private static void RegisterHarnessMappers(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, AntigravitySpanMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, ClaudeSpanMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, CodexSpanMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, CopilotSpanMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, DroidSpanMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, OpenCodeSpanMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, PiSpanMapper>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, AntigravityTranscriptMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, ClaudeTranscriptMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, ClineMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, CodexRolloutMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, CommandCodeMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, CopilotCliMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, CopilotVscodeMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, CursorMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, DevinMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, DroidSessionMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, KiloMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, KimiCodeMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, PiMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, RooCodeMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessMapper, WarpMapper>());
    }
}
