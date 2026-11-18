using KyberWeave.Application.RunStart;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Hooks.Arbiter;
using KyberWeave.Data.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KyberWeave.Application.Arbiter;

/// <summary>Registers the Arbiter's use cases.</summary>
public static class ArbiterApplicationServiceCollectionExtensions
{
    /// <summary>Registers the Arbiter services with <c>TryAdd</c>, so a host or test that registered its own first keeps it.</summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddWeaveArbiterApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<IArbiterEvaluatorFactory, Core.Arbiter.ArbiterEvaluatorFactory>();
        services.TryAddSingleton<ICommentTriageFactory, Core.Arbiter.CommentTriageFactory>();
        services.TryAddSingleton<IArbiterKeyService, Core.Arbiter.ArbiterKeyService>();
        services.TryAddSingleton<IArbiterDryRunService, Core.Arbiter.ArbiterDryRunService>();
        services.TryAddSingleton<IOllamaProbe, Core.Arbiter.OllamaProbe>();
        services.TryAddSingleton<IArbiterConfigResolver, Cli.Commands.Arbiter.ArbiterConfigResolver>();
        services.TryAddSingleton<IArbiterSectionSelectionService, ArbiterSectionSelectionService>();
        services.TryAddSingleton<IPlanRootResolver>(sp => new Core.Arbiter.Plans.PlanRootResolver(
            sp.GetRequiredService<IGitRepositoryFacts>(),
            sp.GetRequiredService<IPlanFileResolver>(),
            sp.GetRequiredService<IFileSystem>(),
            sp.GetRequiredService<IPathResolver>()));
        services.TryAddSingleton<IPlanFacts>(sp => new Core.Arbiter.Plans.PlanFacts(
            sp.GetRequiredService<IPlanFileResolver>(),
            sp.GetRequiredService<IFileSystem>()));
        services.TryAddSingleton<IArbiterPlanService, ArbiterPlanService>();
        services.TryAddSingleton<IArbiterTuneService, ArbiterTuneService>();
        services.TryAddSingleton<IArbiterAuditService, ArbiterAuditService>();
        services.TryAddSingleton<IArbiterDoctorService, ArbiterDoctorService>();
        services.TryAddSingleton<IArbiterInstallService, ArbiterInstallService>();
        services.TryAddSingleton<IArbiterRepositoryLocator, Core.Arbiter.ArbiterRepositoryLocator>();
        services.TryAddSingleton<IArbiterUninstaller, Cli.Commands.Arbiter.ArbiterUninstaller>();
        services.TryAddSingleton<IArbiterHarnessGating, Cli.Commands.Arbiter.ArbiterHarnessGating>();
        services.TryAddSingleton<IArbiterInstallLifecycle>(sp => new Core.Arbiter.ArbiterInstallLifecycle(() =>
            Squad.SquadServiceCollectionExtensions.CreateLifecycle(
                sp,
                sp.GetService<ISquadInstanceReleaseSource>() ?? sp.GetRequiredService<ISquadReleaseSource>())));
        _ = services.AddWeaveArbiterHookHost();
        return services;
    }

    /// <summary>
    /// Registers the hook host: the decision engine, the harness adapters, the hook command and the two guards. The host
    /// config loader is the one port left to the entry app, because the loader still lives in the legacy engine.
    /// </summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    internal static IServiceCollection AddWeaveArbiterHookHost(this IServiceCollection services)
    {
        services.TryAddSingleton<IArbiterHostConfig, ArbiterHostConfig>();
        services.TryAddSingleton<IHookGitFactsFactory, HookGitFactsFactory>();
        services.TryAddSingleton<ArbiterHookDecisionEngine>();
        services.TryAddSingleton<IHookDecisionEngine>(sp => sp.GetRequiredService<ArbiterHookDecisionEngine>());
        services.TryAddSingleton<IContextualHookDecisionEngine>(sp => sp.GetRequiredService<ArbiterHookDecisionEngine>());
        services.TryAddSingleton<IArbiterProviderSource>(sp => sp.GetRequiredService<ArbiterHookDecisionEngine>());
        services.TryAddSingleton(sp => HarnessAdapterRegistry.CreateDefault(
            sp.GetRequiredService<IHookDecisionEngine>(),
            sp.GetRequiredService<IFactoryTranscriptSource>()));
        services.TryAddSingleton(sp =>
        {
            TimeProvider clock = sp.GetRequiredService<TimeProvider>();
            return new HookCommand(
                sp.GetRequiredService<HarnessAdapterRegistry>(),
                sp.GetRequiredService<IArbiterHostConfig>(),
                sp.GetRequiredService<IDispatchClaimStore>(),
                sp.GetRequiredService<IClaudeHookCoverage>(),
                sp.GetRequiredService<IProcessEnvironment>(),
                () => ArbiterRecordId.New(clock.GetUtcNow()),
                sp.GetRequiredService<ITaskPromptServiceFactory>());
        });
        services.TryAddSingleton<IArbiterHookHost>(sp => sp.GetRequiredService<HookCommand>());
        services.TryAddSingleton<IArbiterServeService, ArbiterServeService>();
        services.TryAddSingleton<ArbiterRootResolver>();
        services.TryAddSingleton<IArbiterServeContextFactory, ArbiterServeContextFactory>();
        return services.AddWeaveArbiterGuards();
    }

    /// <summary>
    /// Registers the two guards and only the adapters they read through. The guards run on every tool call of a subagent,
    /// so a host that needs nothing else registers this instead of the whole application layer. The host configuration
    /// loader is the one port left to the entry app.
    /// </summary>
    /// <param name="services">The collection to populate.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddWeaveArbiterGuards(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        _ = services.AddWeaveRunData();
        _ = services.AddWeaveRunStartState();
        services.TryAddSingleton<IRepositoryTreeProbe>(_ => new FileRepositoryTreeProbe());
        services.TryAddSingleton<IKyberWeaveConfigRepository>(sp =>
            new FileKyberWeaveConfigRepository(sp.GetRequiredService<IRepositoryTreeProbe>()));
        services.TryAddSingleton<IWorkerBudgetStore, FileWorkerBudgetStore>();
        services.TryAddSingleton<IFileLinkProbe, FileLinkProbe>();
        services.TryAddSingleton<IGitRepositoryFacts, GitFacts>();
        services.TryAddSingleton<IConductorGuard, ConductorGuard>();
        services.TryAddSingleton<IWorkerGuard, WorkerGuard>();
        return services;
    }
}
