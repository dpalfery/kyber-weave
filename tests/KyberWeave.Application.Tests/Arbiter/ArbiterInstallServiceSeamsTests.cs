using KyberWeave.Application.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Arbiter;

/// <summary>
/// The install command's preparation over the shared path, file-system and environment fakes: the root it plans for is
/// resolved against the working directory the test names, never the real one the test process happens to run in.
/// </summary>
public sealed class ArbiterInstallServiceSeamsTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string Home = "/kw/home";
    private const string OverrideEndpoint = "https://openrouter.ai/api/v1";
    private const string OverrideOrigin = "https://openrouter.ai";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory, homeDirectory: Home);
    private readonly FakeCredentialStore _store = new();

    public ArbiterInstallServiceSeamsTests()
    {
        _files.CreateDirectory(WorkingDirectory);
        _files.CreateDirectory(Home);
        _paths = new FakePathResolver(_files);
    }

    /// <summary>
    /// A relative <c>--path</c> is resolved against the working directory the environment seam names. Before the class
    /// took the path seam it resolved the option with the process's own working directory, so a test could only prove
    /// this by starting in the directory it wanted.
    /// </summary>
    [Fact]
    public void Prepare_ResolvesARelativeProjectPathAgainstTheInjectedWorkingDirectory()
    {
        ArbiterInstallService service = Service();

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: "repo", projectScope: true));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);
        Assert.Equal(WorkingDirectory + "/repo", prepared.Plan.Root);
    }

    /// <summary>An absolute option is its own root, whatever the working directory is.</summary>
    [Fact]
    public void Prepare_KeepsAnAbsoluteProjectPathUnchanged()
    {
        ArbiterInstallService service = Service();

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: "/elsewhere/repo", projectScope: true));

        Assert.Equal("/elsewhere/repo", prepared.Plan?.Root);
    }

    /// <summary>The global scope plans against the home directory the environment seam names.</summary>
    [Fact]
    public void Prepare_PlansTheGlobalScopeAtTheInjectedHomeDirectory()
    {
        ArbiterInstallService service = Service();

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: false));

        Assert.Equal(Home, prepared.Plan?.Root);
        Assert.Equal(SquadDeploymentScope.Global, prepared.Plan?.Scope);
    }

    /// <summary>
    /// The endpoint, its origin and the key all follow the effective provider: the host config names
    /// <c>none</c>, the per-user override names <c>systemone</c> at the override endpoint, and the only
    /// stored key sits under the override origin, so the run completes with no key demand.
    /// </summary>
    [Fact]
    public void Prepare_SystemoneWithOverrideEndpoint_ResolvesTheKeyFromTheOverrideOrigin()
    {
        _store.Seed(OverrideOrigin, "override-key");
        ArbiterInstallService service = Service(OverrideToSystemone());

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: true, providerOption: "systemone"));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);
        Assert.Equal(OverrideEndpoint, prepared.Plan.Endpoint);
        Assert.Equal(OverrideOrigin, prepared.Plan.Origin);
        Assert.False(
            prepared.Events.OfType<ArbiterInstallError>().Any(error => error.Message.Contains(ArbiterKeyResolver.MissingKeyCode, StringComparison.Ordinal)),
            "No key error is expected when the override origin holds a key.");
    }

    /// <summary>
    /// The environment key serves the override origin only because the key service receives the
    /// un-merged repository configuration: the merged endpoint differs from the repository's, so the
    /// override endpoint is the one the environment key is bound to.
    /// </summary>
    [Fact]
    public void Prepare_OverrideEndpoint_ServesTheEnvironmentKeyForTheOverrideOriginOnly()
    {
        ArbiterInstallService service = Service(
            OverrideToSystemone(),
            readVariable: name => string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key" : null);

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: true, providerOption: "systemone"));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);
        Assert.Equal(OverrideEndpoint, prepared.Plan.Endpoint);
        Assert.Equal(OverrideOrigin, prepared.Plan.Origin);
    }

    /// <summary>
    /// A repository-configured endpoint never receives the environment key: with no override and the
    /// host config naming a foreign endpoint, the run stops with the key finding naming that origin.
    /// </summary>
    [Fact]
    public void Prepare_RepositoryEndpoint_NeverServesTheEnvironmentKey()
    {
        const string repositoryOrigin = "https://repo.example.net";
        ArbiterInstallService service = Service(
            configs: new RepositoryEndpointConfigs("https://repo.example.net/v1"),
            readVariable: name => string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key" : null);

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: true, providerOption: "systemone"));

        Assert.Equal(2, prepared.ExitCode);
        Assert.Null(prepared.Plan);
        ArbiterInstallError error = Assert.Single(prepared.Events.OfType<ArbiterInstallError>());
        Assert.Contains(ArbiterKeyResolver.MissingKeyCode, error.Message, StringComparison.Ordinal);
        Assert.Contains(repositoryOrigin, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A malformed per-user override stops the run fail-closed: one error, no plan, before anything
    /// is read or written.
    /// </summary>
    [Fact]
    public void Prepare_MalformedOverride_StopsWithOneError()
    {
        FakeArbiterUserSettings userSettings = new()
        {
            Override = (_, _) => throw new YamlDotNet.Core.YamlException("the test override is malformed"),
        };
        ArbiterInstallService service = Service(userSettings);

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: true, providerOption: "systemone"));

        Assert.Equal(1, prepared.ExitCode);
        Assert.Null(prepared.Plan);
        Assert.Single(prepared.Events.OfType<ArbiterInstallError>());
    }

    /// <summary>
    /// Omitting <c>--provider</c> keeps the configured kind and demands no key, even with an override
    /// present: only the endpoint, origin and key resolution become override-aware, never the key demand.
    /// </summary>
    [Fact]
    public void Prepare_OmittedProvider_KeepsNoneAndSkipsTheKey()
    {
        ArbiterInstallService service = Service(OverrideToSystemone());

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: true));

        Assert.Equal(0, prepared.ExitCode);
        ArbiterInstallProviderLine provider = Assert.Single(prepared.Events.OfType<ArbiterInstallProviderLine>());
        Assert.Equal("none", provider.KindName);
        Assert.True(provider.Kept);
        Assert.Contains("takes precedence", provider.OverrideNote ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(prepared.Events.OfType<ArbiterInstallError>());
    }

    /// <summary>
    /// A non-dry-run install verifies the key against the override endpoint: the environment key
    /// serves the override origin through the key service, so no stored key is needed and no key
    /// error is reported. Reverting <c>Verify</c> to the static resolver, which binds the
    /// environment key to the TypeSafe origin only, fails this test.
    /// </summary>
    [Fact]
    public void Apply_VerifyWithOverrideEndpointAndEnvironmentKey_SucceedsWithoutAStoredKey()
    {
        Func<string, string?> readVariable = name =>
            string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key-for-verify" : null;
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);
        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        ArbiterInstallService applyService = Service(
            OverrideToSystemone(),
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(0, applied.ExitCode);
        Assert.Empty(applied.Events.OfType<ArbiterInstallError>());
    }

    /// <summary>
    /// An override that turns malformed between <c>Prepare</c> and <c>Verify</c> surfaces as the
    /// malformed override, not as a missing key: the key service swallows the malformation by
    /// design, so <c>Verify</c> validates the override explicitly first.
    /// </summary>
    [Fact]
    public void Apply_MalformedOverrideAfterPrepare_ReportsTheMalformedOverride()
    {
        Func<string, string?> readVariable = name =>
            string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key-for-verify" : null;
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);
        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        FakeArbiterUserSettings malformed = new()
        {
            Override = (_, home) => throw new YamlDotNet.Core.YamlException(
                $"[KW-ARB-CONFIG-001] The user override '{ArbiterUserSettings.GetPath(home)}' is malformed in this test."),
        };
        ArbiterInstallService applyService = Service(
            malformed,
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.Contains(ArbiterUserSettings.GetPath(Home), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no key resolves", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unparseable endpoint in the per-user override stops <c>Prepare</c> fail-closed with a
    /// configuration error naming the override file instead of falling back to the TypeSafe origin.
    /// </summary>
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://openrouter.ai/api/v1")]
    [InlineData("")]
    public void Prepare_InvalidOverrideEndpoint_FailsClosedWithConfigError(string invalidEndpoint)
    {
        FakeArbiterUserSettings userSettings = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = invalidEndpoint },
        };
        ArbiterInstallService service = Service(userSettings);

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: true, providerOption: "systemone"));

        Assert.Equal(1, prepared.ExitCode);
        Assert.Null(prepared.Plan);
        ArbiterInstallError error = Assert.Single(prepared.Events.OfType<ArbiterInstallError>());
        Assert.Contains(RuleValidator.MalformedSection, error.Message, StringComparison.Ordinal);
        Assert.Contains(ArbiterUserSettings.GetPath(Home), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterKeyResolver.MissingKeyCode, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterKeyResolver.TypeSafeOrigin, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Verify</c> reads the per-user override once and reuses that effective provider for key
    /// resolution, so a later read cannot switch the endpoint between validation and key binding.
    /// </summary>
    [Fact]
    public void Apply_VerifyReadsOverrideOnce_SucceedsWhenSubsequentReadWouldDiffer()
    {
        Func<string, string?> readVariable = name =>
            string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key-for-verify" : null;
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);
        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        int reads = 0;
        FakeArbiterUserSettings flipping = new()
        {
            Override = (repository, _) =>
            {
                reads++;
                string endpoint = reads == 1 ? OverrideEndpoint : "https://other.example.com/v1";
                return repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = endpoint };
            },
        };
        ArbiterInstallService applyService = Service(
            flipping,
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(0, applied.ExitCode);
        Assert.Empty(applied.Events.OfType<ArbiterInstallError>());
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// Conversely, when the single override read in <c>Verify</c> yields a different endpoint than
    /// the plan recorded, a later change back to the planned endpoint cannot bypass verification.
    /// </summary>
    [Fact]
    public void Apply_VerifyReadsOverrideOnce_FailsWhenSingleReadDiffersFromPlanEndpoint()
    {
        Func<string, string?> readVariable = name =>
            string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key-for-verify" : null;
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);
        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        int reads = 0;
        FakeArbiterUserSettings flipping = new()
        {
            Override = (repository, _) =>
            {
                reads++;
                string endpoint = reads == 1 ? "https://other.example.com/v1" : OverrideEndpoint;
                return repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = endpoint };
            },
        };
        ArbiterInstallService applyService = Service(
            flipping,
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.Contains("no key resolves", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// An override whose endpoint becomes unparseable between <c>Prepare</c> and <c>Verify</c>
    /// fails closed with the configuration error naming the override file.
    /// </summary>
    [Fact]
    public void Apply_InvalidOverrideEndpointAfterPrepare_ReportsConfigErrorNamingOverrideFile()
    {
        Func<string, string?> readVariable = name =>
            string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key-for-verify" : null;
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);
        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        FakeArbiterUserSettings invalidEndpoint = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = "not-a-url" },
        };
        ArbiterInstallService applyService = Service(
            invalidEndpoint,
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.Contains(RuleValidator.MalformedSection, error.Message, StringComparison.Ordinal);
        Assert.Contains(ArbiterUserSettings.GetPath(Home), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no key resolves", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>Prepare</c> reads the per-user override once even when resolving the key for a
    /// systemone provider.
    /// </summary>
    [Fact]
    public void Prepare_ReadsOverrideOnce()
    {
        int reads = 0;
        FakeArbiterUserSettings counting = new()
        {
            Override = (repository, _) =>
            {
                reads++;
                return repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = OverrideEndpoint };
            },
        };
        ArbiterInstallService service = Service(
            counting,
            readVariable: name => string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key" : null);

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: null, projectScope: true, providerOption: "systemone"));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// An unparseable endpoint in the host configuration itself also stops <c>Prepare</c>
    /// fail-closed naming the host configuration path rather than falling back to the TypeSafe origin.
    /// </summary>
    [Fact]
    public void Prepare_InvalidHostConfigEndpoint_FailsClosedNamingHostConfig()
    {
        const string hostConfigPath = "/kw/work/repo/.kyber-weave/kyber-weave.yml";
        ArbiterInstallService service = Service(
            configs: new RepositoryEndpointConfigs("not-a-url", hostConfigPath));

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: "repo", projectScope: true, providerOption: "systemone"));

        Assert.Equal(1, prepared.ExitCode);
        Assert.Null(prepared.Plan);
        ArbiterInstallError error = Assert.Single(prepared.Events.OfType<ArbiterInstallError>());
        Assert.Contains(RuleValidator.MalformedSection, error.Message, StringComparison.Ordinal);
        Assert.Contains(hostConfigPath, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ArbiterKeyResolver.TypeSafeOrigin, error.Message, StringComparison.Ordinal);
    }

    private static FakeArbiterUserSettings OverrideToSystemone() =>
        new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = OverrideEndpoint },
        };

    private static ArbiterInstallRequest Request(
        string? pathOption,
        bool projectScope,
        string? providerOption = null,
        string? arbiterKey = null,
        bool keyStdin = false,
        bool dryRun = true) =>
        new(
            RepositoryPath: pathOption ?? ".",
            PathOption: pathOption,
            ProjectScope: projectScope,
            TargetToken: "claude",
            ConfigOption: null,
            ProviderOption: providerOption,
            ArbiterKey: arbiterKey,
            KeyStdin: keyStdin,
            DryRun: dryRun);

    private ArbiterInstallService Service(
        IArbiterUserSettings? userSettings = null,
        IArbiterKeyService? keys = null,
        IArbiterConfigResolver? configs = null,
        Func<string, string?>? readVariable = null,
        IArbiterInstallLifecycle? lifecycle = null)
    {
        IArbiterUserSettings effectiveSettings = userSettings ?? new NoOverride();
        FakeProcessEnvironment environment = readVariable is null
            ? _environment
            : new FakeProcessEnvironment(
                currentDirectory: WorkingDirectory,
                homeDirectory: Home,
                readVariable: readVariable);
        return new ArbiterInstallService(
            environment,
            _paths,
            new SilentUserConfig(),
            configs ?? new ProductConfigs(),
            effectiveSettings,
            new NoKeyInput(),
            _store,
            _files,
            new FakeSquadStateStore(new FakeSquadUserPaths(Home)),
            lifecycle ?? new NoLifecycle(),
            new FakeSquadGlobalRootResolver(Home),
            new PresentBinary(),
            new NoScaffolder(),
            keys ?? new ArbiterKeyService(_store, environment, effectiveSettings));
    }

    /// <summary>The shipped provider config, with no per-user override over it.</summary>
    private sealed class NoOverride : IArbiterUserSettings
    {
        public ArbiterProviderConfig ApplyTo(ArbiterProviderConfig repository, string homeDirectory) => repository;
    }

    /// <summary>The host configuration naming a repository endpoint, instead of the shipped default.</summary>
    private sealed class RepositoryEndpointConfigs(string endpoint, string? configPath = null) : IArbiterConfigResolver
    {
        public bool TryResolve(
            string path,
            string? configFile,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? configPath)
        {
            config = Load(path, configFile, false).Config!;
            configPath = null;
            return true;
        }

        public KyberWeaveConfig? TryLoadRepository(string root) => Config();

        public KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope) =>
            new(true, Config(), configPath: configPath);

        public KyberWeaveConfig LoadFromYaml(string yaml) => Config();

        private KyberWeaveConfig Config() => new()
        {
            Arbiter = new ArbiterConfig
            {
                Enabled = true,
                Provider = new ArbiterProviderConfig(ArbiterProviderKind.None, endpoint, "jev-1.14.0", 3000),
            },
        };
    }

    /// <summary>The user configuration an install would read; this test never reaches it, so it answers nothing.</summary>
    private sealed class SilentUserConfig : IUserHostConfigStore
    {
        public string EnsureUserConfig(string home) => string.Empty;

        public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
        {
        }

        public void RemoveGlobalTarget(string home, string target)
        {
        }

        public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
    }

    /// <summary>The shipped configuration for every load, so the provider choice is the product default.</summary>
    private sealed class ProductConfigs : IArbiterConfigResolver
    {
        public bool TryResolve(
            string path,
            string? configFile,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? configPath)
        {
            config = KyberWeaveConfigDefaults.Product;
            configPath = null;
            return true;
        }

        public KyberWeaveConfig? TryLoadRepository(string root) => KyberWeaveConfigDefaults.Product;

        public KyberWeaveConfigLoadResult Load(string root, string? configFile, bool projectScope) =>
            new(true, KyberWeaveConfigDefaults.Product);

        public KyberWeaveConfig LoadFromYaml(string yaml) => KyberWeaveConfigDefaults.Product;
    }

    /// <summary>No key is ever offered, so the preparation never asks a person for one.</summary>
    private sealed class NoKeyInput : IArbiterKeyInput
    {
        public string? ReadFromStandardInput() => null;

        public string? PromptHidden() => null;
    }

    /// <summary>A preparation-only test never deploys, so this lifecycle must never be called.</summary>
    private sealed class NoLifecycle : IArbiterInstallLifecycle
    {
        public Task<SquadLifecycleResult> InstallAsync(
            string root,
            SquadDeploymentScope scope,
            SquadTarget target,
            SquadReceipt? existingReceipt,
            bool dryRun,
            SquadArbiterWiring wiring,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A preparation must not deploy.");
    }

    /// <summary>An install the test drives through <c>Apply</c> answers successfully without writing a receipt.</summary>
    private sealed class SucceedingLifecycle : IArbiterInstallLifecycle
    {
        public Task<SquadLifecycleResult> InstallAsync(
            string root,
            SquadDeploymentScope scope,
            SquadTarget target,
            SquadReceipt? existingReceipt,
            bool dryRun,
            SquadArbiterWiring wiring,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SquadLifecycleResult(true));
    }

    /// <summary>The binary is reported as present, so the verification adds no warning.</summary>
    private sealed class PresentBinary : IArbiterBinaryProbe
    {
        public string CliVersion => "1.0.0";

        public ToolProbeResult Probe() => new(true, "1.0.0", null);
    }

    /// <summary>A preparation-only test never scaffolds; the branch that would is skipped by the dry run.</summary>
    private sealed class NoScaffolder : IDocsScaffoldingService
    {
        public ScaffoldResult Scaffold(
            string repoRoot,
            string? docsRoot = null,
            string owner = "unassigned",
            bool force = false,
            bool kyberStandards = false,
            bool reviewGates = true,
            IReadOnlyList<string>? standards = null) =>
            throw new InvalidOperationException("A preparation must not scaffold.");
    }
}
