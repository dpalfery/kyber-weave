using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Where <c>arbiter uninstall</c> looks, what it removes and what it says, over the shared path, file-system,
/// environment, credential and console fakes: nothing here touches the disk or the host's console.
/// </summary>
public sealed class ArbiterUninstallCommandSeamTests
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

    [Fact]
    public void Execute_GlobalAndProject_RefusesWithExitCodeTwo()
    {
        int exit = Run(new ArbiterUninstallSettings { Global = true, Project = true, Target = "claude" });

        Assert.Equal(2, exit);
        Assert.Contains("mutually exclusive", _console.Output, StringComparison.Ordinal);
        Assert.Empty(_uninstaller.Uninstalled);
    }

    [Fact]
    public void Execute_NoPath_UninstallsTheGlobalScopeAtTheHomeDirectory()
    {
        _uninstaller.Deployed = true;

        int exit = Run(new ArbiterUninstallSettings { Target = "claude" });

        Assert.Equal(0, exit);
        Assert.Equal((Home, SquadDeploymentScope.Global, "claude"), Assert.Single(_uninstaller.Uninstalled));
        Assert.Equal((Home, "claude"), Assert.Single(_userConfig.Removed));
        Assert.Contains($"Removed Arbiter hooks for target 'claude' from {Home}.", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_ProjectWithARelativePath_ResolvesItAgainstTheWorkingDirectory()
    {
        _files.CreateDirectory("/kw/work/repo");
        _uninstaller.Deployed = true;

        int exit = Run(new ArbiterUninstallSettings { Project = true, PathOption = "repo", Target = "claude" });

        Assert.Equal(0, exit);
        Assert.Equal(("/kw/work/repo", SquadDeploymentScope.Project, "claude"), Assert.Single(_uninstaller.Uninstalled));
        Assert.Empty(_userConfig.Removed);
    }

    [Fact]
    public void Execute_ARootThatDoesNotExist_ReportsNothingToUninstallAndReturnsZero()
    {
        _uninstaller.Deployed = true;

        int exit = Run(new ArbiterUninstallSettings { Project = true, PathOption = "missing", Target = "claude" });

        Assert.Equal(0, exit);
        Assert.Contains("No Arbiter deployment found at /kw/work/missing", _console.Output, StringComparison.Ordinal);
        Assert.Empty(_uninstaller.Uninstalled);
    }

    [Fact]
    public void Execute_AnUnknownTarget_NamesTheRosterAndReturnsTwo()
    {
        int exit = Run(new ArbiterUninstallSettings { Target = "nonesuch" });

        Assert.Equal(2, exit);
        Assert.Contains("Unknown target 'nonesuch'", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RemoveKey_DeletesTheStoredKeyForTheEndpointOrigin()
    {
        _uninstaller.Deployed = true;

        int exit = Run(new ArbiterUninstallSettings { Target = "claude", RemoveKey = true });

        Assert.Equal(0, exit);
        Assert.Equal(ArbiterKeyResolver.TypeSafeOrigin, Assert.Single(_credentials.Deletes));
    }

    [Fact]
    public void Execute_RemoveKey_DeletesTheKeyForTheOverrideEndpointOrigin()
    {
        const string overrideOrigin = "https://openrouter.ai";
        _uninstaller.Deployed = true;
        _credentials.Seed(ArbiterKeyResolver.TypeSafeOrigin, "typesafe-key");
        _credentials.Seed(overrideOrigin, "override-key");
        FakeArbiterUserSettings userSettings = new()
        {
            Override = (repository, _) => repository with { Kind = ArbiterProviderKind.Systemone, Endpoint = "https://openrouter.ai/api/v1" },
        };

        int exit = Run(new ArbiterUninstallSettings { Target = "claude", RemoveKey = true }, userSettings);

        Assert.Equal(0, exit);
        Assert.Equal([overrideOrigin], _credentials.Deletes);
        Assert.Contains(overrideOrigin, _console.Output, StringComparison.Ordinal);
        Assert.Equal("typesafe-key", _credentials.Read(ArbiterKeyResolver.TypeSafeOrigin));
        Assert.Null(_credentials.Read(overrideOrigin));
    }

    /// <summary>
    /// A malformed per-user override stops <c>--remove-key</c> fail-closed: one error line and no
    /// deletion, so a key the tool cannot attribute to an origin survives.
    /// </summary>
    [Fact]
    public void Execute_RemoveKey_MalformedOverride_DeletesNothing()
    {
        _uninstaller.Deployed = true;
        _credentials.Seed(ArbiterKeyResolver.TypeSafeOrigin, "typesafe-key");
        FakeArbiterUserSettings userSettings = new()
        {
            Override = (_, _) => throw new YamlDotNet.Core.YamlException("the test override is malformed"),
        };

        int exit = Run(new ArbiterUninstallSettings { Target = "claude", RemoveKey = true }, userSettings);

        Assert.Equal(1, exit);
        Assert.Empty(_credentials.Deletes);
        Assert.Contains("malformed", _console.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("typesafe-key", _credentials.Read(ArbiterKeyResolver.TypeSafeOrigin));
    }

    [Fact]
    public void Execute_APathNamingTheHomeDirectory_IsTheGlobalScope()
    {
        _uninstaller.Deployed = true;

        _ = Run(new ArbiterUninstallSettings { Path = Home, Target = "claude" });

        Assert.Equal(SquadDeploymentScope.Global, Assert.Single(_uninstaller.Uninstalled).Scope);
    }

    private int Run(ArbiterUninstallSettings settings, IArbiterUserSettings? userSettings = null)
    {
        FakeProcessEnvironment environment = new(currentDirectory: WorkingDirectory, homeDirectory: Home);
        ArbiterUninstallCommand command = new(
            _uninstaller,
            new FakeSquadStateStore(null!),
            environment,
            _userConfig,
            new DefaultConfigs(),
            _credentials,
            _files,
            new FakePathResolver(_files),
            _console,
            userSettings ?? new FakeArbiterUserSettings());
        return command.Execute(null!, settings);
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
}
