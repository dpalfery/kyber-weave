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
using KyberWeave.Data.Arbiter;
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
        Assert.Contains("does not match the planned endpoint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://other.example.com/v1", error.Message, StringComparison.Ordinal);
        Assert.Contains(OverrideEndpoint, error.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// When both origins hold stored keys, <c>Verify</c> must fail closed if the re-read
    /// effective endpoint diverges from the endpoint the plan recorded instead of checking the
    /// stale planned endpoint against a mismatched override.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_WhenOverrideSwitchesToDifferentEndpointWithStoredKeysOnBothOrigins()
    {
        const string secondOrigin = "https://other.example.com";
        const string secondEndpoint = "https://other.example.com/v1";
        _store.Seed(OverrideOrigin, "key-a");
        _store.Seed(secondOrigin, "key-b");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);

        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        FakeArbiterUserSettings switched = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = secondEndpoint },
        };
        ArbiterInstallService applyService = Service(
            switched,
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.Contains(OverrideEndpoint, error.Message, StringComparison.Ordinal);
        Assert.Contains(secondEndpoint, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Even within the same origin, a path change between <c>Prepare</c> and <c>Verify</c>
    /// diverges from the planned endpoint and must fail closed.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_WhenOverrideChangesPathOnSameOrigin()
    {
        const string changedPathEndpoint = "https://openrouter.ai/api/v2";
        _store.Seed(OverrideOrigin, "override-key");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);

        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        FakeArbiterUserSettings switched = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = changedPathEndpoint },
        };
        ArbiterInstallService applyService = Service(
            switched,
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.Contains(OverrideEndpoint, error.Message, StringComparison.Ordinal);
        Assert.Contains(changedPathEndpoint, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Switching from a remote planned endpoint to a loopback endpoint before <c>Verify</c>
    /// must also fail closed rather than verifying against the stale remote plan.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_WhenOverrideSwitchesToLoopbackAfterRemotePlan()
    {
        const string loopbackEndpoint = "http://127.0.0.1:11434/v1";
        _store.Seed(OverrideOrigin, "override-key");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);

        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        FakeArbiterUserSettings switched = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = loopbackEndpoint },
        };
        ArbiterInstallService applyService = Service(
            switched,
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.Contains(OverrideEndpoint, error.Message, StringComparison.Ordinal);
        Assert.Contains(loopbackEndpoint, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// When planned or effective endpoints (or their origins) exceed 200 characters, the
    /// <c>Verify</c> mismatch error truncates them instead of embedding unbounded strings.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_LongMismatchEndpointsAndOrigins_AreTruncatedInError()
    {
        string longPlannedOrigin = "https://" + string.Join('.', Enumerable.Repeat(new string('a', 55), 4)) + ".example.com";
        string longPlannedEndpoint = longPlannedOrigin + "/v1/planned";
        string longEffectiveOrigin = "https://" + string.Join('.', Enumerable.Repeat(new string('b', 55), 4)) + ".example.com";
        string longEffectiveEndpoint = longEffectiveOrigin + "/v1/effective";
        _store.Seed(longPlannedOrigin, "key-planned");
        _store.Seed(longEffectiveOrigin, "key-effective");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(longPlannedEndpoint);

        ArbiterInstallService prepareService = Service(
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        FakeArbiterUserSettings switched = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = longEffectiveEndpoint },
        };
        ArbiterInstallService applyService = Service(
            switched,
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.Contains("does not match the planned endpoint", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(longPlannedEndpoint[..200] + "...", error.Message, StringComparison.Ordinal);
        Assert.Contains(longEffectiveEndpoint[..200] + "...", error.Message, StringComparison.Ordinal);
        Assert.Contains(longPlannedOrigin[..200] + "...", error.Message, StringComparison.Ordinal);
        Assert.Contains(longEffectiveOrigin[..200] + "...", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(longPlannedEndpoint, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(longEffectiveEndpoint, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(longPlannedOrigin, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(longEffectiveOrigin, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Long endpoints that match through the 200-character display cap but diverge afterwards
    /// fail closed in <c>Verify</c>, remain bounded, and disambiguate the colliding displays.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_LongEndpointsDifferingAfterTruncationLimit_StillFailClosedAndTruncate()
    {
        string sharedPrefix = "https://openrouter.ai/api/" + new string('x', 200);
        string plannedEndpoint = sharedPrefix + "/planned";
        string effectiveEndpoint = sharedPrefix + "/effective";
        _store.Seed(OverrideOrigin, "override-key");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);

        ArbiterInstallService prepareService = Service(
            new FakeArbiterUserSettings
            {
                Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = plannedEndpoint },
            },
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        ArbiterInstallService applyService = Service(
            new FakeArbiterUserSettings
            {
                Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = effectiveEndpoint },
            },
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        (string effectiveDisplay, string effectiveOriginDisplay, string plannedDisplay, string plannedOriginDisplay) =
            ParseVerifyMismatchDisplays(error.Message);
        Assert.NotEqual(effectiveDisplay, plannedDisplay);
        Assert.StartsWith(sharedPrefix[..200] + "...", effectiveDisplay, StringComparison.Ordinal);
        Assert.StartsWith(sharedPrefix[..200] + "...", plannedDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {sharedPrefix.Length + 1}", effectiveDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {sharedPrefix.Length + 1}", plannedDisplay, StringComparison.Ordinal);
        Assert.Contains($"len {effectiveEndpoint.Length}", effectiveDisplay, StringComparison.Ordinal);
        Assert.Contains($"len {plannedEndpoint.Length}", plannedDisplay, StringComparison.Ordinal);
        Assert.Contains("sha256:", effectiveDisplay, StringComparison.Ordinal);
        Assert.Contains("sha256:", plannedDisplay, StringComparison.Ordinal);
        Assert.Equal(OverrideOrigin, effectiveOriginDisplay);
        Assert.Equal(OverrideOrigin, plannedOriginDisplay);
        Assert.DoesNotContain("/planned", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/effective", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 175), error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Long endpoints with identical length and divergence index past the 200-character cap are
    /// still disambiguated by content hash so the error never shows identical strings.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_LongEndpointsDifferingAtSameIndexAndLength_AreDisambiguated()
    {
        string sharedPrefix = "https://openrouter.ai/api/" + new string('x', 200);
        string plannedEndpoint = sharedPrefix + "/v1";
        string effectiveEndpoint = sharedPrefix + "/v2";
        _store.Seed(OverrideOrigin, "override-key");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);

        ArbiterInstallService prepareService = Service(
            new FakeArbiterUserSettings
            {
                Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = plannedEndpoint },
            },
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        ArbiterInstallService applyService = Service(
            new FakeArbiterUserSettings
            {
                Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = effectiveEndpoint },
            },
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        (string effectiveDisplay, _, string plannedDisplay, _) = ParseVerifyMismatchDisplays(error.Message);
        Assert.NotEqual(effectiveDisplay, plannedDisplay);
        Assert.StartsWith(sharedPrefix[..200] + "...", effectiveDisplay, StringComparison.Ordinal);
        Assert.StartsWith(sharedPrefix[..200] + "...", plannedDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {sharedPrefix.Length + 2}, len {effectiveEndpoint.Length}, sha256:", effectiveDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {sharedPrefix.Length + 2}, len {plannedEndpoint.Length}, sha256:", plannedDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/v2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Long origins that share the first 200 characters and diverge afterwards are also
    /// disambiguated in the <c>Verify</c> mismatch error while keeping both origins bounded.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_LongOriginsDifferingAfterTruncationLimit_AreDisambiguated()
    {
        string sharedHostPrefix = string.Join('.', Enumerable.Repeat(new string('a', 55), 4));
        string plannedOrigin = "https://" + sharedHostPrefix + ".p.example.com";
        string effectiveOrigin = "https://" + sharedHostPrefix + ".e.example.com";
        string plannedEndpoint = plannedOrigin + "/v1";
        string effectiveEndpoint = effectiveOrigin + "/v1";
        _store.Seed(plannedOrigin, "key-planned");
        _store.Seed(effectiveOrigin, "key-effective");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(plannedEndpoint);

        ArbiterInstallService prepareService = Service(
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        ArbiterInstallService applyService = Service(
            new FakeArbiterUserSettings
            {
                Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = effectiveEndpoint },
            },
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        (string effectiveEndpointDisplay, string effectiveOriginDisplay, string plannedEndpointDisplay, string plannedOriginDisplay) =
            ParseVerifyMismatchDisplays(error.Message);
        int expectedDiffIndex = ("https://" + sharedHostPrefix + ".").Length;
        Assert.NotEqual(effectiveEndpointDisplay, plannedEndpointDisplay);
        Assert.NotEqual(effectiveOriginDisplay, plannedOriginDisplay);
        Assert.StartsWith(plannedOrigin[..200] + "...", effectiveOriginDisplay, StringComparison.Ordinal);
        Assert.StartsWith(plannedOrigin[..200] + "...", plannedOriginDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {expectedDiffIndex}, len {effectiveOrigin.Length}, sha256:", effectiveOriginDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {expectedDiffIndex}, len {plannedOrigin.Length}, sha256:", plannedOriginDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain(".p.example.com", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".e.example.com", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(plannedOrigin, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(effectiveOrigin, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Values that differ only in line endings (and therefore collapse to the same single-line
    /// representation) are still disambiguated in the <c>Verify</c> mismatch error.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_EndpointsDifferingOnlyInLineEndings_AreDisambiguated()
    {
        const string spaceEndpoint = "https://openrouter.ai/api/a b";
        const string multilineEndpoint = "https://openrouter.ai/api/a\r\nb";
        _store.Seed(OverrideOrigin, "override-key");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);

        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        ArbiterInstallPlan multilinePlan = prepared.Plan with
        {
            Endpoint = multilineEndpoint,
        };
        ArbiterInstallService applyService = Service(
            new FakeArbiterUserSettings
            {
                Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = spaceEndpoint },
            },
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(multilinePlan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        (string effectiveDisplay, string effectiveOriginDisplay, string plannedDisplay, string plannedOriginDisplay) =
            ParseVerifyMismatchDisplays(error.Message);
        Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", error.Message, StringComparison.Ordinal);
        Assert.NotEqual(effectiveDisplay, plannedDisplay);
        Assert.StartsWith(spaceEndpoint, effectiveDisplay, StringComparison.Ordinal);
        Assert.StartsWith(spaceEndpoint, plannedDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {spaceEndpoint.IndexOf(' ', StringComparison.Ordinal)}, len {spaceEndpoint.Length}, sha256:", effectiveDisplay, StringComparison.Ordinal);
        Assert.Contains($"differs at index {spaceEndpoint.IndexOf(' ', StringComparison.Ordinal)}, len {multilineEndpoint.Length}, sha256:", plannedDisplay, StringComparison.Ordinal);
        Assert.Equal(OverrideOrigin, effectiveOriginDisplay);
        Assert.Equal(OverrideOrigin, plannedOriginDisplay);
    }

    /// <summary>
    /// Line endings in a planned endpoint or origin are collapsed to spaces in the <c>Verify</c>
    /// mismatch error so the message remains single-line.
    /// </summary>
    [Fact]
    public void Apply_VerifyFailsClosed_MultilinePlannedValuesInMismatchError_AreSanitizedToSingleLine()
    {
        _store.Seed(OverrideOrigin, "override-key");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);

        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);

        ArbiterInstallPlan multilinePlan = prepared.Plan with
        {
            Endpoint = "https://other.example.com/a\r\nb",
            Origin = "https://other.example.com\nc",
        };
        ArbiterInstallService applyService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(multilinePlan, CancellationToken.None);

        Assert.Equal(1, applied.ExitCode);
        ArbiterInstallError error = Assert.Single(applied.Events.OfType<ArbiterInstallError>());
        Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://other.example.com/a b", error.Message, StringComparison.Ordinal);
        Assert.Contains("https://other.example.com c", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An override file whose endpoint carries surrounding whitespace resolves to the trimmed
    /// endpoint and still receives the environment key across both <c>Prepare</c> and <c>Verify</c>.
    /// </summary>
    [Fact]
    public void PrepareAndApply_OverrideEndpointWithTrailingSpace_HonorsEnvironmentKey()
    {
        string overridePath = ArbiterUserSettings.GetPath(Home);
        _files.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        _files.WriteAllText(overridePath, $"provider:\n  kind: systemone\n  endpoint: \"{OverrideEndpoint} \"\n");
        Func<string, string?> readVariable = name =>
            string.Equals(name, ArbiterKeyResolver.EnvVarName, StringComparison.Ordinal) ? "env-key" : null;
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);
        FileArbiterUserSettings userSettings = new(_files);

        ArbiterInstallService service = Service(
            userSettings,
            configs: configs,
            readVariable: readVariable,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = service.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);
        Assert.Equal(OverrideEndpoint, prepared.Plan.Endpoint);
        Assert.Equal(OverrideOrigin, prepared.Plan.Origin);

        ArbiterInstallApplied applied = service.Apply(prepared.Plan, CancellationToken.None);

        Assert.Equal(0, applied.ExitCode);
        Assert.Empty(applied.Events.OfType<ArbiterInstallError>());
    }

    /// <summary>
    /// When both the host configuration and the per-user override file specify the same invalid
    /// endpoint value, the configuration error must still name the override file rather than
    /// masking the override behind raw string equality.
    /// </summary>
    [Fact]
    public void Prepare_BothHostConfigAndOverrideSetSameInvalidEndpoint_NamesOverrideFile()
    {
        const string hostConfigPath = "/kw/work/repo/.kyber-weave/kyber-weave.yml";
        string overridePath = ArbiterUserSettings.GetPath(Home);
        _files.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        _files.WriteAllText(overridePath, "provider:\n  kind: systemone\n  endpoint: not-a-url\n");
        ArbiterInstallService service = Service(
            new FileArbiterUserSettings(_files),
            configs: new RepositoryEndpointConfigs("not-a-url", hostConfigPath));

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: "repo", projectScope: true, providerOption: "systemone"));

        Assert.Equal(1, prepared.ExitCode);
        Assert.Null(prepared.Plan);
        ArbiterInstallError error = Assert.Single(prepared.Events.OfType<ArbiterInstallError>());
        Assert.Contains(RuleValidator.MalformedSection, error.Message, StringComparison.Ordinal);
        Assert.Contains(overridePath, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostConfigPath, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the host configuration carries an invalid endpoint and the per-user override only
    /// overrides another field (without setting <c>endpoint</c>), the error names the host
    /// configuration rather than blaming the override file.
    /// </summary>
    [Fact]
    public void Prepare_HostConfigInvalidAndOverrideSetsOnlyModel_NamesHostConfig()
    {
        const string hostConfigPath = "/kw/work/repo/.kyber-weave/kyber-weave.yml";
        string overridePath = ArbiterUserSettings.GetPath(Home);
        _files.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        _files.WriteAllText(overridePath, "provider:\n  model: nimble\n");
        ArbiterInstallService service = Service(
            new FileArbiterUserSettings(_files),
            configs: new RepositoryEndpointConfigs("not-a-url", hostConfigPath));

        ArbiterInstallPrepared prepared = service.Prepare(Request(pathOption: "repo", projectScope: true, providerOption: "systemone"));

        Assert.Equal(1, prepared.ExitCode);
        Assert.Null(prepared.Plan);
        ArbiterInstallError error = Assert.Single(prepared.Events.OfType<ArbiterInstallError>());
        Assert.Contains(RuleValidator.MalformedSection, error.Message, StringComparison.Ordinal);
        Assert.Contains(hostConfigPath, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(overridePath, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An explicit endpoint-overridden flag with no merge delegate still names the override file,
    /// which is the case of a supplied endpoint equal to the repository endpoint.
    /// </summary>
    [Fact]
    public void Prepare_ExplicitEndpointOverriddenWithoutMerge_NamesOverrideFile()
    {
        const string hostConfigPath = "/kw/work/repo/.kyber-weave/kyber-weave.yml";
        string overridePath = ArbiterUserSettings.GetPath(Home);
        ArbiterInstallService service = Service(
            new FakeArbiterUserSettings { EndpointOverridden = true },
            configs: new RepositoryEndpointConfigs("not-a-url", hostConfigPath));

        ArbiterInstallPrepared prepared = service.Prepare(
            Request(pathOption: "repo", projectScope: true, providerOption: "systemone"));

        Assert.Equal(1, prepared.ExitCode);
        ArbiterInstallError error = Assert.Single(prepared.Events.OfType<ArbiterInstallError>());
        Assert.Contains(overridePath, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(hostConfigPath, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A planned endpoint that differs from the effective one only by surrounding space still verifies.
    /// </summary>
    [Fact]
    public void Apply_Verify_PlanEndpointTrailingSpace_MatchesTheTrimmedEffectiveEndpoint()
    {
        _store.Seed(OverrideOrigin, "override-key");
        IArbiterConfigResolver configs = new RepositoryEndpointConfigs(ArbiterProviderConfig.DefaultNone.Endpoint);
        ArbiterInstallService prepareService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallPrepared prepared = prepareService.Prepare(
            Request(pathOption: null, projectScope: false, providerOption: "systemone", dryRun: false));

        Assert.Equal(0, prepared.ExitCode);
        Assert.NotNull(prepared.Plan);
        ArbiterInstallPlan spaced = prepared.Plan with { Endpoint = prepared.Plan.Endpoint + " " };

        ArbiterInstallService applyService = Service(
            OverrideToSystemone(),
            configs: configs,
            lifecycle: new SucceedingLifecycle());
        ArbiterInstallApplied applied = applyService.Apply(spaced, CancellationToken.None);

        Assert.Equal(0, applied.ExitCode);
    }

    private static FakeArbiterUserSettings OverrideToSystemone() =>
        new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = OverrideEndpoint },
        };

    private static (string EffectiveEndpoint, string EffectiveOrigin, string PlannedEndpoint, string PlannedOrigin) ParseVerifyMismatchDisplays(
        string message)
    {
        const string effectiveEndpointPrefix = "Verification failed: the effective provider endpoint '";
        const string originPrefix = "' (origin '";
        const string plannedEndpointPrefix = "') does not match the planned endpoint '";
        const string suffix = "').";

        Assert.StartsWith(effectiveEndpointPrefix, message, StringComparison.Ordinal);
        int effectiveEndpointEnd = message.IndexOf(originPrefix, effectiveEndpointPrefix.Length, StringComparison.Ordinal);
        Assert.True(effectiveEndpointEnd >= 0, $"Unexpected Verify mismatch message format: {message}");

        int effectiveOriginStart = effectiveEndpointEnd + originPrefix.Length;
        int effectiveOriginEnd = message.IndexOf(plannedEndpointPrefix, effectiveOriginStart, StringComparison.Ordinal);
        Assert.True(effectiveOriginEnd >= 0, $"Unexpected Verify mismatch message format: {message}");

        int plannedEndpointStart = effectiveOriginEnd + plannedEndpointPrefix.Length;
        int plannedEndpointEnd = message.IndexOf(originPrefix, plannedEndpointStart, StringComparison.Ordinal);
        Assert.True(plannedEndpointEnd >= 0, $"Unexpected Verify mismatch message format: {message}");

        int plannedOriginStart = plannedEndpointEnd + originPrefix.Length;
        Assert.EndsWith(suffix, message, StringComparison.Ordinal);
        int plannedOriginEnd = message.Length - suffix.Length;
        Assert.True(plannedOriginEnd >= plannedOriginStart, $"Unexpected Verify mismatch message format: {message}");

        return (
            message[effectiveEndpointPrefix.Length..effectiveEndpointEnd],
            message[effectiveOriginStart..effectiveOriginEnd],
            message[plannedEndpointStart..plannedEndpointEnd],
            message[plannedOriginStart..plannedOriginEnd]);
    }

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

        public ArbiterUserProviderResolution Resolve(ArbiterProviderConfig repository, string homeDirectory) =>
            new(repository, EndpointOverridden: false);
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

        public Task<ScaffoldResult> ScaffoldAsync(
            string repoRoot,
            string? docsRoot = null,
            string owner = "unassigned",
            bool force = false,
            bool kyberStandards = false,
            bool reviewGates = true,
            IReadOnlyList<string>? standards = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A preparation must not scaffold.");
    }
}
