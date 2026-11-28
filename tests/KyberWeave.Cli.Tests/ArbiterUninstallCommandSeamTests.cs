using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Where <c>arbiter uninstall</c> looks, what it removes and what it says, over the shared path, file-system,
/// environment, credential and console fakes: nothing here touches the disk or the host's console.
/// </summary>
public sealed class ArbiterUninstallCommandSeamTests : IDisposable
{
    private const string WorkingDirectory = "/kw/work";
    private const string Home = "/kw/home";

    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly RecordingUninstaller _uninstaller = new();
    private readonly RecordingUserConfig _userConfig = new();
    private readonly FakeCredentialStore _credentials = new();

    public ArbiterUninstallCommandSeamTests()
    {
        _console.Profile.Width = 400;
        _files.CreateDirectory(Home);
        _files.CreateDirectory(WorkingDirectory);
    }

    public void Dispose() => _console.Dispose();

    [Fact]
    public async Task Execute_GlobalAndProject_RefusesWithExitCodeTwo()
    {
        int exit = await RunAsync(new ArbiterUninstallSettings { Global = true, Project = true, Target = "claude" });

        Assert.Equal(2, exit);
        Assert.Contains("mutually exclusive", _console.Output, StringComparison.Ordinal);
        Assert.Empty(_uninstaller.Uninstalled);
    }

    [Fact]
    public async Task Execute_NoPath_UninstallsTheGlobalScopeAtTheHomeDirectory()
    {
        _uninstaller.Deployed = true;

        int exit = await RunAsync(new ArbiterUninstallSettings { Target = "claude" });

        Assert.Equal(0, exit);
        Assert.Equal((Home, SquadDeploymentScope.Global, "claude"), Assert.Single(_uninstaller.Uninstalled));
        Assert.Equal((Home, "claude"), Assert.Single(_userConfig.Removed));
        Assert.Contains($"Removed Arbiter hooks for target 'claude' from {Home}.", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ProjectWithARelativePath_ResolvesItAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory("/kw/work/repo");
        _uninstaller.Deployed = true;

        int exit = await RunAsync(new ArbiterUninstallSettings { Project = true, PathOption = "repo", Target = "claude" });

        Assert.Equal(0, exit);
        Assert.Equal(("/kw/work/repo", SquadDeploymentScope.Project, "claude"), Assert.Single(_uninstaller.Uninstalled));
        Assert.Empty(_userConfig.Removed);
    }

    [Fact]
    public async Task Execute_ARootThatDoesNotExist_ReportsNothingToUninstallAndReturnsZero()
    {
        _uninstaller.Deployed = true;

        int exit = await RunAsync(new ArbiterUninstallSettings { Project = true, PathOption = "missing", Target = "claude" });

        Assert.Equal(0, exit);
        Assert.Contains("No Arbiter deployment found at /kw/work/missing", _console.Output, StringComparison.Ordinal);
        Assert.Empty(_uninstaller.Uninstalled);
    }

    [Fact]
    public async Task Execute_AnUnknownTarget_NamesTheRosterAndReturnsTwo()
    {
        int exit = await RunAsync(new ArbiterUninstallSettings { Target = "nonesuch" });

        Assert.Equal(2, exit);
        Assert.Contains("Unknown target 'nonesuch'", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_RemoveKey_DeletesTheStoredKeyForTheEndpointOrigin()
    {
        _uninstaller.Deployed = true;

        int exit = await RunAsync(new ArbiterUninstallSettings { Target = "claude", RemoveKey = true });

        Assert.Equal(0, exit);
        Assert.Equal(ArbiterKeyResolver.TypeSafeOrigin, Assert.Single(_credentials.Deletes));
    }

    [Fact]
    public async Task Execute_RemoveKey_DeletesTheKeyForTheOverrideEndpointOrigin()
    {
        const string overrideOrigin = "https://openrouter.ai";
        _uninstaller.Deployed = true;
        _credentials.Seed(ArbiterKeyResolver.TypeSafeOrigin, "typesafe-key");
        _credentials.Seed(overrideOrigin, "override-key");
        FakeArbiterUserSettings userSettings = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = "https://openrouter.ai/api/v1" },
        };

        int exit = await RunAsync(new ArbiterUninstallSettings { Target = "claude", RemoveKey = true }, userSettings);

        Assert.Equal(0, exit);
        Assert.Equal([overrideOrigin], _credentials.Deletes);
        Assert.Contains(overrideOrigin, _console.Output, StringComparison.Ordinal);
        Assert.Equal("typesafe-key", await _credentials.ReadAsync(ArbiterKeyResolver.TypeSafeOrigin));
        Assert.Null(await _credentials.ReadAsync(overrideOrigin));
    }

    /// <summary>
    /// A malformed per-user override stops <c>--remove-key</c> fail-closed: one error line and no
    /// deletion, so a key the tool cannot attribute to an origin survives.
    /// </summary>
    [Fact]
    public async Task Execute_RemoveKey_MalformedOverride_DeletesNothing()
    {
        _uninstaller.Deployed = true;
        _credentials.Seed(ArbiterKeyResolver.TypeSafeOrigin, "typesafe-key");
        FakeArbiterUserSettings userSettings = new()
        {
            Override = (_, home) => throw new YamlDotNet.Core.YamlException(
                $"[KW-ARB-CONFIG-001] The user override '{ArbiterUserSettings.GetPath(home)}' is malformed in this test."),
        };

        int exit = await RunAsync(new ArbiterUninstallSettings { Target = "claude", RemoveKey = true }, userSettings);

        Assert.Equal(1, exit);
        Assert.Empty(_credentials.Deletes);
        Assert.Contains(ArbiterUserSettings.GetPath(Home), _console.Output, StringComparison.Ordinal);
        Assert.Equal("typesafe-key", await _credentials.ReadAsync(ArbiterKeyResolver.TypeSafeOrigin));
    }

    /// <summary>
    /// An unparseable endpoint in the per-user override must fail closed instead of falling back
    /// to the TypeSafe origin and deleting the wrong credential.
    /// </summary>
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://openrouter.ai/api/v1")]
    [InlineData("")]
    public async Task Execute_RemoveKey_InvalidOverrideEndpoint_FailsClosedAndDeletesNothing(string invalidEndpoint)
    {
        const string overrideOrigin = "https://openrouter.ai";
        _uninstaller.Deployed = true;
        _credentials.Seed(ArbiterKeyResolver.TypeSafeOrigin, "typesafe-key");
        _credentials.Seed(overrideOrigin, "override-key");
        string overridePath = ArbiterUserSettings.GetPath(Home);
        _files.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        _files.WriteAllText(overridePath, $"provider:\n  kind: systemone\n  endpoint: \"{invalidEndpoint}\"\n");

        int exit = await RunAsync(
            new ArbiterUninstallSettings { Target = "claude", RemoveKey = true },
            new FileArbiterUserSettings(_files));

        Assert.Equal(1, exit);
        Assert.Empty(_credentials.Deletes);
        Assert.Contains(RuleValidator.MalformedSection, _console.Output, StringComparison.Ordinal);
        Assert.Contains(overridePath, _console.Output, StringComparison.Ordinal);
        Assert.Equal("typesafe-key", await _credentials.ReadAsync(ArbiterKeyResolver.TypeSafeOrigin));
        Assert.Equal("override-key", await _credentials.ReadAsync(overrideOrigin));
    }

    /// <summary>
    /// An unparseable endpoint in the host configuration itself also stops <c>--remove-key</c>
    /// fail-closed naming that host configuration file and deletes nothing.
    /// </summary>
    [Fact]
    public async Task Execute_RemoveKey_InvalidHostConfigEndpoint_FailsClosedNamingHostConfig()
    {
        const string hostConfigPath = "/kw/home/.kyber-weave/kyber-weave.yml";
        _uninstaller.Deployed = true;
        _credentials.Seed(ArbiterKeyResolver.TypeSafeOrigin, "typesafe-key");
        DefaultConfigs configs = new()
        {
            Loaded = new KyberWeaveConfigLoadResult(
                true,
                new KyberWeaveConfig
                {
                    Arbiter = new ArbiterConfig
                    {
                        Enabled = true,
                        Provider = new ArbiterProviderConfig(ArbiterProviderKind.Systemone, "not-a-url", "jev-1.14.0", 3000),
                    },
                },
                configPath: hostConfigPath),
        };

        int exit = await RunAsync(new ArbiterUninstallSettings { Target = "claude", RemoveKey = true }, configs: configs);

        Assert.Equal(1, exit);
        Assert.Empty(_credentials.Deletes);
        Assert.Contains(RuleValidator.MalformedSection, _console.Output, StringComparison.Ordinal);
        Assert.Contains(hostConfigPath, _console.Output, StringComparison.Ordinal);
        Assert.Equal("typesafe-key", await _credentials.ReadAsync(ArbiterKeyResolver.TypeSafeOrigin));
    }

    /// <summary>
    /// When both the host configuration and the per-user override file specify the same invalid
    /// endpoint, <c>--remove-key</c> fails closed naming the per-user override file.
    /// </summary>
    [Fact]
    public async Task Execute_RemoveKey_BothHostConfigAndOverrideSetSameInvalidEndpoint_FailsClosedNamingOverrideFile()
    {
        const string hostConfigPath = "/kw/home/.kyber-weave/kyber-weave.yml";
        _uninstaller.Deployed = true;
        _credentials.Seed(ArbiterKeyResolver.TypeSafeOrigin, "typesafe-key");
        string overridePath = ArbiterUserSettings.GetPath(Home);
        _files.CreateDirectory(Path.GetDirectoryName(overridePath)!);
        _files.WriteAllText(overridePath, "provider:\n  kind: systemone\n  endpoint: not-a-url\n");
        DefaultConfigs configs = new()
        {
            Loaded = new KyberWeaveConfigLoadResult(
                true,
                new KyberWeaveConfig
                {
                    Arbiter = new ArbiterConfig
                    {
                        Enabled = true,
                        Provider = new ArbiterProviderConfig(ArbiterProviderKind.Systemone, "not-a-url", "jev-1.14.0", 3000),
                    },
                },
                configPath: hostConfigPath),
        };

        int exit = await RunAsync(
            new ArbiterUninstallSettings { Target = "claude", RemoveKey = true },
            new FileArbiterUserSettings(_files),
            configs);

        Assert.Equal(1, exit);
        Assert.Empty(_credentials.Deletes);
        Assert.Contains(RuleValidator.MalformedSection, _console.Output, StringComparison.Ordinal);
        Assert.Contains(overridePath, _console.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(hostConfigPath, _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_APathNamingTheHomeDirectory_IsTheGlobalScope()
    {
        _uninstaller.Deployed = true;

        _ = await RunAsync(new ArbiterUninstallSettings { Path = Home, Target = "claude" });

        Assert.Equal(SquadDeploymentScope.Global, Assert.Single(_uninstaller.Uninstalled).Scope);
    }

    private Task<int> RunAsync(
        ArbiterUninstallSettings settings,
        IArbiterUserSettings? userSettings = null,
        IArbiterConfigResolver? configs = null)
    {
        FakeProcessEnvironment environment = new(currentDirectory: WorkingDirectory, homeDirectory: Home);
        ArbiterUninstallCommand command = new(
            _uninstaller,
            new FakeSquadStateStore(null!),
            environment,
            _userConfig,
            configs ?? new DefaultConfigs(),
            _credentials,
            _files,
            new FakePathResolver(_files),
            _console,
            userSettings ?? new FakeArbiterUserSettings());
        return command.ExecuteAsync(null!, settings, CancellationToken.None);
    }

    private sealed class RecordingUninstaller : IArbiterUninstaller
    {
        public bool Deployed { get; set; }

        public List<(string Root, SquadDeploymentScope Scope, string Target)> Uninstalled { get; } = [];

        public void Uninstall(string root, SquadDeploymentScope scope, string target) => Uninstalled.Add((root, scope, target));

        public bool HasDeployment(string root, SquadTarget target, SquadDeploymentScope scope) => Deployed;

        public bool AnotherTargetIsDeployed(string root, SquadTarget target) => false;
    }

    private sealed class RecordingUserConfig : IUserHostConfigStore
    {
        public List<(string Home, string Target)> Removed { get; } = [];

        public string EnsureUserConfig(string home) => string.Empty;

        public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
        {
        }

        public void RemoveGlobalTarget(string home, string target) => Removed.Add((home, target));

        public IReadOnlyList<string> ReadGlobalTargets(string home) => [];
    }

    private sealed class DefaultConfigs : IArbiterConfigResolver
    {
        public KyberWeaveConfigLoadResult? Loaded { get; init; }

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
            Loaded ?? new KyberWeaveConfigLoadResult(true, KyberWeaveConfigDefaults.Product);

        public KyberWeaveConfig LoadFromYaml(string yaml) => KyberWeaveConfigDefaults.Product;
    }
}
