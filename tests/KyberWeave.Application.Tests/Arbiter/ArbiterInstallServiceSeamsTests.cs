using KyberWeave.Application.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
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

    private readonly InMemoryFileSystem _files = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory, homeDirectory: Home);

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

    private static ArbiterInstallRequest Request(string? pathOption, bool projectScope) =>
        new(
            RepositoryPath: pathOption ?? ".",
            PathOption: pathOption,
            ProjectScope: projectScope,
            TargetToken: "claude",
            ConfigOption: null,
            ProviderOption: null,
            ArbiterKey: null,
            KeyStdin: false,
            DryRun: true);

    private ArbiterInstallService Service() =>
        new(
            _environment,
            _paths,
            new SilentUserConfig(),
            new ProductConfigs(),
            new NoOverride(),
            new NoKeyInput(),
            new NoCredentials(),
            _files,
            new FakeSquadStateStore(new FakeSquadUserPaths(Home)),
            new NoLifecycle(),
            new FakeSquadGlobalRootResolver(Home),
            new PresentBinary(),
            new NoScaffolder());

    /// <summary>The shipped provider config, with no per-user override over it.</summary>
    private sealed class NoOverride : IArbiterUserSettings
    {
        public ArbiterProviderConfig ApplyTo(ArbiterProviderConfig repository, string homeDirectory) => repository;
    }

    /// <summary>No key is stored and none is needed for the shipped provider, so the store is never asked.</summary>
    private sealed class NoCredentials : ICredentialStore
    {
        public string? Read(string origin) => throw new InvalidOperationException("The store is not reached for this request.");

        public void Write(string origin, string secret) => throw new InvalidOperationException("The store is not reached for this request.");

        public void Delete(string origin) => throw new InvalidOperationException("The store is not reached for this request.");
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
