using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The engine, serve and hook composition of <see cref="InMemoryArbiterHost"/>: the production decision engine, the
/// serve tools, the harness adapters and the hook command, each built from the host's container so every port is the
/// in-memory one.
/// </summary>
internal sealed partial class InMemoryArbiterHost
{
    private static readonly Func<HttpMessageHandler> UnscriptedHttp = () =>
        throw new Xunit.Sdk.XunitException("The Arbiter reached for the network, and this test scripted no handler.");

    /// <summary>Gets the repository root a test writes its repository under, platform-qualified and outside the working directory.</summary>
    internal string RepoRoot => ArbiterTestRoots.Under("repo");

    /// <summary>Gets the arbiter state directory under <see cref="RepoRoot"/>, where the ledger and decision log record.</summary>
    internal string ArbiterDirectory => InMemoryArbiterState.Directory;

    /// <summary>Gets the in-flight ledger at <see cref="ArbiterDirectory"/>.</summary>
    internal InFlightLedger Ledger => State.LedgerStore;

    /// <summary>Gets every decision recorded at <see cref="ArbiterDirectory"/> so far.</summary>
    internal IReadOnlyList<ArbiterDecisionRecord> Decisions => State.Records;

    /// <summary>Writes the repository configuration the Arbiter loads from <see cref="RepoRoot"/>.</summary>
    /// <param name="yaml">The configuration text.</param>
    /// <param name="root">The repository root; <see cref="RepoRoot"/> when null.</param>
    /// <returns>The path of the file.</returns>
    internal string WriteRepoConfig(string yaml, string? root = null) =>
        Write(Path.Combine(root ?? RepoRoot, ".kyber-weave", "kyber-weave.yml"), yaml);

    /// <summary>Loads the host configuration the way the project hook does.</summary>
    /// <param name="repoRoot">The repository root.</param>
    internal KyberWeaveConfig LoadHostConfig(string repoRoot) => Get<IArbiterHostConfig>().Load(repoRoot);

    /// <summary>Loads the host configuration for the given hook scope and home.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <param name="scope">The hook scope.</param>
    /// <param name="homeDirectory">The home directory; the host's own when null.</param>
    internal KyberWeaveConfig LoadHostConfig(string repoRoot, HookScope scope, string? homeDirectory) =>
        Get<IArbiterHostConfig>().Load(repoRoot, scope, homeDirectory);

    /// <summary>The production engine over the host's in-memory ports, with the test's replacements for the pieces it names.</summary>
    /// <param name="providerFactory">Replaces the provider the configuration names.</param>
    /// <param name="gitFactsFactory">Replaces the git facts.</param>
    /// <param name="planReaderFactory">Replaces the plan reader.</param>
    /// <param name="homeDirectory">Replaces the home; the host's home when null.</param>
    /// <param name="clock">Replaces the host's clock.</param>
    /// <param name="credentialStoreFactory">Replaces the credential store.</param>
    /// <param name="environment">Replaces the environment variables.</param>
    /// <param name="planRoots">Replaces the plan root resolver.</param>
    internal ArbiterHookDecisionEngine Engine(
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
            clock ?? Clock,
            credentialStoreFactory,
            environment);

    /// <summary>The git facts the hook reads, over the host's in-memory ports.</summary>
    /// <param name="workingDirectory">The directory the hook runs in.</param>
    /// <param name="homeDirectory">The home directory the snapshot store lives under; null for none.</param>
    /// <param name="arbiterDirectory">The arbiter state directory; null for the default.</param>
    internal HookGitFacts HookGitFacts(string workingDirectory, string? homeDirectory = null, string? arbiterDirectory = null) =>
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

    /// <summary>The serve tools over a serve context and the host's in-memory file and triage ports.</summary>
    /// <param name="context">The serve context.</param>
    internal ArbiterTools Tools(ArbiterServeContext context) =>
        new(new ArbiterServeService(
            context,
            Get<IFileTextFacts>(),
            Get<ICommentTriageFactory>(),
            Get<IArbiterSectionSelectionService>(),
            Get<TimeProvider>(),
            Get<IIdGenerator>()));

    /// <summary>The serve tools over a repository root and the engine a test builds, with the host's configuration loader.</summary>
    /// <param name="engine">The decision engine the tools call.</param>
    /// <param name="repoRoot">The root the serve surface answers for; <see cref="RepoRoot"/> when null.</param>
    /// <param name="loadConfig">Loads the configuration; the host's loader when null.</param>
    /// <param name="keyResolved">Answers whether the provider's key resolves; true when null.</param>
    internal ArbiterTools Tools(
        ArbiterHookDecisionEngine engine,
        string? repoRoot = null,
        Func<string, KyberWeaveConfig>? loadConfig = null,
        Func<KyberWeaveConfig, bool>? keyResolved = null) =>
        Tools(new ArbiterServeContext(repoRoot ?? RepoRoot, engine, TextWriter.Null, loadConfig ?? LoadHostConfig, keyResolved ?? (_ => true)));

    /// <summary>Builds the comment command over the container, with the provider a test answers instead of a configured one.</summary>
    /// <param name="providerFactory">Builds the provider for a repository root.</param>
    internal ArbiterCommentCommand CommentCommand(Func<string, DiagnosticReport, IArbiterProvider?> providerFactory)
    {
        ArbiterCommentCommand command = Command<ArbiterCommentCommand>();
        command.ProviderFactory = providerFactory;
        return command;
    }

    /// <summary>The default adapter registry over <paramref name="engine"/> and the host's transcript reader.</summary>
    /// <param name="engine">The decision engine the adapters call.</param>
    internal HarnessAdapterRegistry Registry(IHookDecisionEngine engine) =>
        HarnessAdapterRegistry.CreateDefault(engine, Get<IFactoryTranscriptSource>());

    /// <summary>The command adapters over <paramref name="engine"/> and the host's transcript reader.</summary>
    /// <param name="engine">The decision engine the adapters call.</param>
    internal IReadOnlyList<IHarnessHookAdapter> CommandAdapters(IHookDecisionEngine engine) =>
        CommandHookAdapters.All(engine, Get<IFactoryTranscriptSource>());

    /// <summary>The hook command over a registry and a project-scope configuration loader.</summary>
    /// <param name="registry">The harness adapters.</param>
    /// <param name="configLoader">Loads the project configuration; a test passes one that counts its calls.</param>
    /// <param name="newDecisionId">Builds a decision id; null uses <paramref name="ids"/>.</param>
    /// <param name="ids">The identifier seam; the host's when null.</param>
    /// <param name="prompts">The prompt service factory; null installs none, so no dispatch is expanded.</param>
    internal HookCommand HookCommand(
        HarnessAdapterRegistry registry,
        Func<string, KyberWeaveConfig>? configLoader = null,
        Func<string>? newDecisionId = null,
        IIdGenerator? ids = null,
        ITaskPromptServiceFactory? prompts = null) =>
        new(
            registry,
            configLoader is null ? Get<IArbiterHostConfig>() : new DelegateHostConfig(configLoader, Get<IArbiterHostConfig>()),
            Get<IDispatchClaimStore>(),
            Get<IClaudeHookCoverage>(),
            Get<IProjectDispatchHookProbe>(),
            Get<IProcessEnvironment>(),
            ids ?? Get<IIdGenerator>(),
            newDecisionId,
            prompts);

    /// <summary>The project-scope configuration comes from a test's loader; every other answer is the host's.</summary>
    private sealed class DelegateHostConfig(Func<string, KyberWeaveConfig> loader, IArbiterHostConfig inner) : IArbiterHostConfig
    {
        public KyberWeaveConfig Load(string repoRoot, HookScope scope = HookScope.Project, string? homeDirectory = null) =>
            scope == HookScope.Project ? loader(repoRoot) : inner.Load(repoRoot, scope, homeDirectory);

        public KyberWeaveConfig? LoadProject(string repoRoot) => inner.LoadProject(repoRoot);

        public string ResolveHomeDirectory(string? homeDirectory) => inner.ResolveHomeDirectory(homeDirectory);
    }
}
