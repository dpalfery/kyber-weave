using KyberWeave.Contracts.Paths;
using KyberWeave.Application;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KyberWeave.Tests.Arbiter;

/// <summary>Resolves the Arbiter's registered ports the way the entry app does, for tests that build a command, an engine or a tool directly.</summary>
internal static class ArbiterTestServices
{
    private static readonly Lazy<IServiceProvider> Provider = new(() => new ServiceCollection().AddArbiterHost().BuildServiceProvider());

    private static readonly Lazy<ArbiterTestHost> Host = new(() => ArbiterTestHost.Create());

    /// <summary>Builds an Arbiter command from the container, over the real adapters, the way the host builds it.</summary>
    /// <typeparam name="T">The command type.</typeparam>
    internal static T Command<T>()
        where T : class =>
        Host.Value.Command<T>();

    /// <summary>Builds the comment command over the container, with the provider a test answers instead of a configured one.</summary>
    /// <param name="providerFactory">Builds the provider for a repository root.</param>
    internal static ArbiterCommentCommand CommentCommand(
        Func<string, KyberWeave.Core.Diagnostics.DiagnosticReport, KyberWeave.Core.Arbiter.Providers.IArbiterProvider?> providerFactory)
    {
        ArbiterCommentCommand command = Command<ArbiterCommentCommand>();
        command.ProviderFactory = providerFactory;
        return command;
    }

    /// <summary>Resolves <typeparamref name="T"/> from the application layer's registrations.</summary>
    /// <summary>Resolves <typeparamref name="T"/> from the entry app's registrations.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    internal static T Get<T>()
        where T : notnull =>
        Provider.Value.GetRequiredService<T>();

    /// <summary>Loads the host configuration the way the project hook does.</summary>
    internal static KyberWeaveConfig LoadHostConfig(string repoRoot) => Get<IArbiterHostConfig>().Load(repoRoot);

    /// <summary>Loads the host configuration for the given hook scope and home.</summary>
    internal static KyberWeaveConfig LoadHostConfig(string repoRoot, HookScope scope, string? homeDirectory) =>
        Get<IArbiterHostConfig>().Load(repoRoot, scope, homeDirectory);

    /// <summary>The production engine over the registered ports, with the test's replacements for the pieces it names.</summary>
    internal static ArbiterHookDecisionEngine Engine(
        Func<KyberWeaveConfig, IArbiterProvider>? providerFactory = null,
        Func<string, IArbiterGitFacts>? gitFactsFactory = null,
        Func<string, IArbiterPlanReader>? planReaderFactory = null,
        Func<string>? homeDirectory = null,
        TimeProvider? clock = null,
        Func<ICredentialStore>? credentialStoreFactory = null,
        Func<string, string?>? environment = null,
        IPlanRootResolver? planRoots = null) =>
        new(
            Get<IArbiterEvaluatorFactory>(),
            Get<IArbiterStateFactory>(),
            Get<IArbiterProviderFactory>(),
            Get<IArbiterUserSettings>(),
            Get<IPlanFacts>(),
            planRoots ?? Get<IPlanRootResolver>(),
            Get<IHookGitFactsFactory>(),
            Get<ICredentialStore>(),
            Get<IArbiterHostConfig>(),
            Get<IProcessEnvironment>(),
            Get<IBackgroundDispatchStore>(),
            Get<IIdGenerator>(),
            new ReviewSeatRecorder(Get<IWritableFileSystem>(), Get<IPathResolver>()),
            providerFactory,
            gitFactsFactory,
            planReaderFactory,
            homeDirectory,
            clock,
            credentialStoreFactory,
            environment);

    /// <summary>The default registry over <paramref name="engine"/> and the real transcript reader.</summary>
    internal static HarnessAdapterRegistry Registry(IHookDecisionEngine engine) =>
        HarnessAdapterRegistry.CreateDefault(engine, Get<IFactoryTranscriptSource>());

    /// <summary>Builds the default adapter registry over a transcript source the test supplies.</summary>
    /// <param name="engine">The decision engine the adapters call.</param>
    /// <param name="transcripts">Reads the Factory session transcripts.</param>
    internal static HarnessAdapterRegistry Registry(IHookDecisionEngine engine, IFactoryTranscriptSource transcripts) =>
        HarnessAdapterRegistry.CreateDefault(engine, transcripts);

    /// <summary>The command adapters over <paramref name="engine"/> and the real transcript reader.</summary>
    internal static IReadOnlyList<IHarnessHookAdapter> CommandAdapters(IHookDecisionEngine engine) =>
        CommandHookAdapters.All(engine, Get<IFactoryTranscriptSource>());

    /// <summary>The Factory adapter over <paramref name="engine"/> and the real transcript reader.</summary>
    internal static FactoryHookAdapter FactoryAdapter(IHookDecisionEngine engine) =>
        new(engine, Get<IFactoryTranscriptSource>());

    /// <summary>The hook command over a registry and a project-scope configuration loader.</summary>
    /// <param name="registry">The harness adapters.</param>
    /// <param name="configLoader">Loads the project configuration; a test passes one that counts its calls.</param>
    /// <param name="newDecisionId">Builds a decision id; null uses <paramref name="ids"/>.</param>
    /// <param name="ids">The identifier seam; the registered one when null.</param>
    /// <param name="prompts">The prompt service factory; null installs none, so no dispatch is expanded.</param>
    /// <param name="runContexts">
    /// The run context the hook reads paths from; null installs <see cref="PassthroughRunContexts"/>, which reads
    /// nothing. An integration test that needs real git and worktrees passes <c>Get&lt;IHookRunContextResolver&gt;()</c>.
    /// </param>
    internal static HookCommand Command(
        HarnessAdapterRegistry registry,
        Func<string, KyberWeaveConfig> configLoader,
        Func<string>? newDecisionId = null,
        IIdGenerator? ids = null,
        ITaskPromptServiceFactory? prompts = null,
        IHookRunContextResolver? runContexts = null) =>
        new(
            registry,
            new DelegateHostConfig(configLoader, Get<IArbiterHostConfig>()),
            Get<IDispatchClaimStore>(),
            Get<IClaudeHookCoverage>(),
            Get<IProcessEnvironment>(),
            ids ?? Get<IIdGenerator>(),
            newDecisionId,
            prompts,
            // A unit test resolves no paths unless it injects a resolver: the real one starts git and reads the
            // operator's home on every hook invocation, which made this project's unit run minutes long.
            runContexts ?? new PassthroughRunContexts());

    /// <summary>The git facts over the real adapters.</summary>
    internal static HookGitFacts HookGitFacts(string workingDirectory, string? homeDirectory = null, string? arbiterDirectory = null) =>
        new(
            Get<IGitRepositoryFacts>(),
            Get<IPlanFileResolver>(),
            Get<IHookSnapshotStore>(),
            Get<IHookFileWindowReader>(),
            Get<IRunCheckout>(),
            Get<IRunStateStore>(),
            Get<IProcessEnvironment>(),
            Get<IPathResolver>(),
            Get<TimeProvider>(),
            workingDirectory,
            homeDirectory,
            arbiterDirectory);

    /// <summary>The serve tools over a serve context and the real file and triage ports.</summary>
    internal static ArbiterTools Tools(ArbiterServeContext context) =>
        new(new ArbiterServeService(context, Get<IFileTextFacts>(), Get<ICommentTriageFactory>(), Get<IArbiterSectionSelectionService>(), Get<TimeProvider>(), Get<IIdGenerator>()));

    /// <summary>The project-scope configuration comes from a test's loader; every other answer is the real one.</summary>
    private sealed class DelegateHostConfig(Func<string, KyberWeaveConfig> loader, IArbiterHostConfig real) : IArbiterHostConfig
    {
        public KyberWeaveConfig Load(string repoRoot, HookScope scope = HookScope.Project, string? homeDirectory = null) =>
            scope == HookScope.Project ? loader(repoRoot) : real.Load(repoRoot, scope, homeDirectory);

        public KyberWeaveConfig? LoadProject(string repoRoot) => real.LoadProject(repoRoot);

        public string ResolveHomeDirectory(string? homeDirectory) => real.ResolveHomeDirectory(homeDirectory);
    }
}
