using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Data.Arbiter.Credentials;
using KyberWeave.Data.Hooks.Arbiter;
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
        services.TryAddSingleton<IArbiterUserSettings>(sp => new FileArbiterUserSettings(sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<ICredentialStore>(_ => OperatingSystemCredentialStore.Create());
        services.TryAddSingleton(sp => new ConfiguredArbiterProviderFactory(sp.GetRequiredService<IArbiterUserSettings>()));
        services.TryAddSingleton<IArbiterStateFactory, FileArbiterStateFactory>();
        services.TryAddSingleton<IGateReportFacts, GateReportFacts>();
        services.TryAddSingleton<IFileTextFacts, FileTextFacts>();
        services.TryAddSingleton<IPlanFileResolver, PlanPathResolver>();
        services.TryAddSingleton<IPlanRootResolver>(sp => new PlanRootResolver(
            sp.GetRequiredService<IGitRepositoryFacts>(),
            sp.GetRequiredService<IPlanFileResolver>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>()));
        services.TryAddSingleton<IPlanFacts>(sp => new PlanFacts(sp.GetRequiredService<IPlanFileResolver>()));
        services.TryAddSingleton<IClaudeHookCoverage, ClaudeHookCoverage>();
        services.TryAddSingleton<IArbiterProviderFactory, SystemOneProviderFactory>();
        services.TryAddSingleton<IArbiterHttpHandlers, SocketArbiterHttpHandlers>();
        services.TryAddSingleton<IFileStatReader, FileStatReader>();
        services.TryAddSingleton<IArbiterUserOverrideWriter>(sp => new FileArbiterUserOverrideWriter(sp.GetRequiredService<IWritableFileSystem>()));
        services.TryAddSingleton<IDispatchClaimStore, FileDispatchClaimStore>();
        services.TryAddSingleton<IBackgroundDispatchStore, FileBackgroundDispatchStore>();
        services.TryAddSingleton<IHookSnapshotStore, FileHookSnapshotStore>();
        services.TryAddSingleton<IHookFileWindowReader, FileHookFileWindowReader>();
        services.TryAddSingleton<IWorkerBudgetStore, FileWorkerBudgetStore>();
        services.TryAddSingleton<IFileLinkProbe, FileLinkProbe>();
        services.TryAddSingleton<IFactoryTranscriptSource, FileFactoryTranscriptSource>();
        return services;
    }
}
