using KyberWeave.Cli;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The decisions <c>arbiter install</c> makes itself, over the shared file-system, path, environment, credential,
/// state, lifecycle, scaffolder, probe and console fakes: the home and project roots, the host config, the key and the
/// deployment all live in memory, so nothing here reads the real home, keychain, process table or console.
/// </summary>
public sealed class ArbiterInstallCommandSeamTests : IDisposable
{
    private const string TypeSafeOrigin = "https://api.typesafe.ai";

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly FakeConsole _streams = new(isInputRedirected: true);
    private readonly FakeCredentialStore _store = new();
    private readonly FakeDocsScaffoldingService _scaffolder = new();
    private readonly SquadGlobalRoots _globalRoots;
    private readonly FakeSquadStateStore _state;
    private readonly FakeArbiterInstallLifecycle _lifecycle;
    private readonly string _home;
    private readonly string _work;
    private readonly ArbiterTestHost _host;

    private string? _keyStdinValue;
    private string? _keyPromptValue;

    public ArbiterInstallCommandSeamTests()
    {
        _home = Path.Combine(_files.Root, "home");
        _work = Path.Combine(_files.Root, "work");
        _files.CreateDirectory(_home);
        _files.CreateDirectory(_work);
        _console.Profile.Width = 400;
        _globalRoots = new SquadGlobalRoots(_ => null, _home, isWindows: false);
        _state = new FakeSquadStateStore(FakeSquadUserPaths.FromHome(_home));
        _lifecycle = new FakeArbiterInstallLifecycle(_state, _globalRoots, _files);
        _host = ArbiterTestHost.Create(RegisterFakes);
    }

    public void Dispose()
    {
        _host.Dispose();
        _console.Dispose();
    }

    // The commands resolve the injected home, file system, config store, state and key input, never the real home,
    // keychain, console or environment.
    private void RegisterFakes(IServiceCollection services)
    {
        services.AddSingleton<IFileSystem>(_files);
        services.AddSingleton<IWritableFileSystem>(_files);
        services.AddSingleton<IPathResolver>(new FakePathResolver(_files));
        services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(
            currentDirectory: _work,
            homeDirectory: _home));
        services.AddSingleton<IUserHostConfigStore>(new FileUserHostConfigStore(_files));
        services.AddSingleton<ICredentialStore>(_store);
        services.AddSingleton<IArbiterKeyInput>(new FakeArbiterKeyInput(() => _keyStdinValue, () => _keyPromptValue));
        services.AddSingleton<ISquadStateStore>(_state);
        services.AddSingleton<ISquadGlobalRootResolver>(_globalRoots);
        services.AddSingleton<IArbiterInstallLifecycle>(_lifecycle);
        services.AddSingleton<IArbiterBinaryProbe>(new FakeArbiterBinaryProbe());
        services.AddSingleton<IDocsScaffoldingService>(_scaffolder);
        services.AddSingleton<IAnsiConsole>(_console);
        services.AddSingleton<IConsole>(_streams);
        services.AddSingleton(new SquadConsoleOptions(IsInteractive: false));
    }

    /// <summary>
    /// An unknown target is client input, not a crash: exit 2 naming the roster.
    /// </summary>
    [Fact]
    public void Install_InvalidTarget_ExitsTwoNamingTheRoster()
    {
        string host = NewDir("install-bad-target");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "hal9000", Provider = "none" }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("hal9000", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kilo", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.Writes);
    }

    /// <summary>
    /// An explicit <c>--global</c> is accepted and behaves exactly like the default
    /// global scope: it writes under the user home, records the target in the user
    /// config, and exits 0. The mutually exclusive <c>--global --project</c> case is
    /// covered by the dedicated rule-id test.
    /// </summary>
    [Fact]
    public void Install_GlobalScope_ExplicitGlobal_IsAcceptedAndWritesToHomeConfigAndGlobalRoot()
    {
        string host = NewDir("install-global");
        new FileUserHostConfigStore(_files).WriteGlobalTargets(_home, ["claude", "warp"]);

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "claude",
                Provider = "none",
                Global = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("global", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ArbiterHookWiring.TrustSteps[SquadTarget.Claude], execution.Output, StringComparison.Ordinal);

        string globalRoot = _globalRoots.ResolveGlobalRoot(SquadTarget.Claude);
        Assert.True(_files.DirectoryExists(globalRoot), "the explicit global install should write beneath the target's global root");

        string config = _files.ReadAllText(UserHostConfigPath.Of(_home));
        Assert.Contains("claude", config, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("global", config, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An empty <c>--provider</c> resolves to <c>none</c>: no key is required and none
    /// is stored. An empty string must not become an unknown-provider failure.
    /// </summary>
    [Fact]
    public void Install_EmptyProvider_ResolvesToNone()
    {
        string host = NewDir("install-empty-provider");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = string.Empty, Project = true }));

        Assert.Equal(0, execution.ExitCode);
        string config = _files.ReadAllText(ConfigPath(host));
        Assert.Contains("none", config, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_store.Entries);
    }

    /// <summary>
    /// Omitting <c>--provider</c> keeps the provider the host config already holds, and the
    /// run says so.
    /// </summary>
    [Fact]
    public void Install_OmittedProvider_KeepsTheExistingProvider()
    {
        string host = NewDir("install-keep-provider");
        WriteProviderConfig(host, "systemone");
        _store.Write(TypeSafeOrigin, "stored-key");

        CommandExecution execution = Capture(() => _host.Command<ArbiterInstallCommand>().Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("kind: systemone", _files.ReadAllText(ConfigPath(host)), StringComparison.Ordinal);
        Assert.Contains("systemone", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kept", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>An explicit <c>--provider</c> replaces the configured provider and reports the change.</summary>
    [Fact]
    public void Install_ExplicitProvider_ChangesTheExistingProvider()
    {
        string host = NewDir("install-change-provider");
        WriteProviderConfig(host, "systemone");

        CommandExecution execution = Capture(() => _host.Command<ArbiterInstallCommand>().Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = "none", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        string config = _files.ReadAllText(ConfigPath(host));
        Assert.Contains("kind: none", config, StringComparison.Ordinal);
        Assert.DoesNotContain("systemone", config, StringComparison.Ordinal);
        Assert.Contains("changed from systemone", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A machine with nothing configured keeps the default of no provider.</summary>
    [Fact]
    public void Install_OmittedProviderOnAFreshMachine_DefaultsToNone()
    {
        string host = NewDir("install-fresh-provider");

        CommandExecution execution = Capture(() => _host.Command<ArbiterInstallCommand>().Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("kind: none", _files.ReadAllText(ConfigPath(host)), StringComparison.Ordinal);
        Assert.Contains("none", execution.Output, StringComparison.Ordinal);
        Assert.Empty(_store.Entries);
    }

    /// <summary>
    /// The key travels into the credential store for the endpoint origin only. It must
    /// appear in no config file, no receipt, no deployed file, and no captured output:
    /// a leaked key in the repo would be committed and pushed.
    /// </summary>
    [Fact]
    public void Install_SystemoneKey_StoredOnlyInCredentialStore()
    {
        const string key = "TS-INSTALL-KEY-7f3a9c1e4b2d";
        string host = NewDir("install-key");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "kilo",
                Provider = "systemone",
                ArbiterKey = key,
                Project = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Equal(key, _store.Read(TypeSafeOrigin));
        Assert.DoesNotContain(key, execution.Output, StringComparison.Ordinal);
        string config = _files.ReadAllText(ConfigPath(host));
        Assert.DoesNotContain(key, config, StringComparison.Ordinal);

        // The lifecycle writes a deployed hook file, so the sweep covers more than the config.
        Assert.NotEmpty(_files.EnumerateFiles(host, "*", recursive: true));
        foreach (string file in _files.EnumerateFiles(host, "*", recursive: true))
        {
            string content = _files.ReadAllText(file);
            Assert.DoesNotContain(key, content, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A remote provider with no key on the flag, on stdin, or in the environment
    /// surfaces the existing <c>KW-ARB-KEY-001</c> rather than rendering dead hooks.
    /// The exit code is deliberately unpinned here: the packet pins the diagnostic.
    /// </summary>
    [Fact]
    public void Install_SystemoneWithoutKey_SurfacesKey001()
    {
        _keyStdinValue = null;
        _keyPromptValue = null;
        string host = NewDir("install-missing-key");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = "systemone" }));

        Assert.Contains(ArbiterStatusCommand.KeyMissing, execution.Output, StringComparison.Ordinal);
        Assert.Null(_store.Read(TypeSafeOrigin));
    }

    /// <summary>
    /// A second identical run is a no-op: exit 0 with a status line naming the target
    /// and no duplicate credential-store write.
    /// </summary>
    [Fact]
    public void Install_SecondIdenticalRun_IsNoOpWithoutDuplicateWrites()
    {
        const string key = "TS-INSTALL-REPEAT-1a2b3c4d5e6f";
        string host = NewDir("install-repeat");
        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();

        CommandExecution first = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "kilo",
                Provider = "systemone",
                ArbiterKey = key,
                Project = true,
            }));
        CommandExecution second = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "kilo",
                Provider = "systemone",
                ArbiterKey = key,
                Project = true,
            }));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("kilo", second.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_store.Writes);
        Assert.Single(_lifecycle.Calls);
    }

    /// <summary>
    /// The config merge is non-destructive: pre-existing comments and unrelated keys
    /// survive byte-for-byte while the provider choice is written.
    /// </summary>
    [Fact]
    public void Install_PreservesPreExistingConfigContent()
    {
        string host = NewDir("install-merge");
        string directory = Path.Combine(host, ".kyber-weave");
        _files.CreateDirectory(directory);
        _files.WriteAllText(
            Path.Combine(directory, "kyber-weave.yml"),
            "# Operator-owned header\n" +
            "squad:\n" +
            "  targets: [kilo]\n" +
            "# keep this comment\n" +
            "custom-unrelated-key: custom-value\n");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = "none", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        string config = _files.ReadAllText(ConfigPath(host));
        Assert.Contains("# Operator-owned header", config, StringComparison.Ordinal);
        Assert.Contains("# keep this comment", config, StringComparison.Ordinal);
        Assert.Contains("custom-unrelated-key: custom-value", config, StringComparison.Ordinal);
        Assert.Contains("targets: [kilo]", config, StringComparison.Ordinal);
        Assert.Contains("none", config, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A default install writes under the user home (global scope) rather than the project
    /// root, and it records the harness in the user config so later refreshes know which
    /// global hooks to maintain. The command stays in the home tree even when the project
    /// path is set, because a global install is intentionally user-scoped.
    /// </summary>
    [Fact]
    public void Install_DefaultScope_GlobalWritesToHomeConfigAndGlobalRoot()
    {
        string host = NewDir("install-global-default");
        new FileUserHostConfigStore(_files).WriteGlobalTargets(_home, ["claude", "warp"]);

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "claude",
                Provider = "none",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("global", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ArbiterHookWiring.TrustSteps[SquadTarget.Claude], execution.Output, StringComparison.Ordinal);

        string globalRoot = _globalRoots.ResolveGlobalRoot(SquadTarget.Claude);
        Assert.True(_files.DirectoryExists(globalRoot), "the default install should write beneath the target's global root");

        string config = _files.ReadAllText(UserHostConfigPath.Of(_home));
        Assert.Contains("claude", config, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("global", config, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A default global install must create the user-level config but must not scaffold the
    /// injected home itself, because the home is a runtime user directory and not the project root
    /// the docs scaffolder is meant to populate.
    /// </summary>
    [Fact]
    public void Install_DefaultGlobalScope_WithoutUserConfig_CreatesUserConfigAndNoDocsScaffold()
    {
        string host = NewDir("install-global-default-empty-home");
        Assert.False(_files.FileExists(UserHostConfigPath.Of(_home)));
        Assert.False(_files.DirectoryExists(Path.Combine(_home, "docs")));
        Assert.False(_files.FileExists(Path.Combine(_home, "AGENTS.md")));

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "claude",
                Provider = "none",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.True(_files.FileExists(UserHostConfigPath.Of(_home)), "the default global install should create the user config");
        Assert.False(_files.DirectoryExists(Path.Combine(_home, "docs")), "a global install must not scaffold docs under the home directory");
        Assert.False(_files.FileExists(Path.Combine(_home, "AGENTS.md")), "a global install must not scaffold AGENTS.md under the home directory");
        Assert.Empty(_scaffolder.ScaffoldedRoots);
    }

    /// <summary>
    /// The D13 table is shaped for each configured harness: the target token, the tier, the
    /// exact path written, the reason code and the trust step or off-switch that still needs
    /// a user action. Output must be informative even when the harness is a fallback-only or
    /// partial case under global scope.
    /// </summary>
    [Fact]
    public void Install_D13Output_ListsTierPathReasonAndTrustStepForEachGlobalHarness()
    {
        string host = NewDir("install-d13");
        new FileUserHostConfigStore(_files).WriteGlobalTargets(_home, ["claude", "warp", "copilot"]);

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "claude",
                Provider = "none",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("tier", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reason", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("warp", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copilot", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chat.useHooks", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>--path</c> is only legal for an explicit project install. A bare global install must
    /// reject it before any side effects so the command cannot accidentally write a project file
    /// or user config from the wrong scope.
    /// </summary>
    [Fact]
    public void Install_PathWithoutProject_ExitsTwoWithScopeError()
    {
        string host = NewDir("install-path-without-project");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Target = "claude", PathOption = host }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("--project", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--path", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_lifecycle.Calls);
    }

    /// <summary>
    /// The scope flags are mutually exclusive: a global install cannot also opt into project
    /// scope. The command must fail with the install-rule id rather than silently picking one.
    /// </summary>
    [Fact]
    public void Install_GlobalAndProject_ExitsTwoWithKwArbInstall001()
    {
        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Target = "claude", Global = true, Project = true }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("KW-ARB-INSTALL-001", execution.Output, StringComparison.Ordinal);
        Assert.Empty(_lifecycle.Calls);
    }

    /// <summary>
    /// A dry run on a bare root previews instead of installing: it must not scaffold
    /// <c>.kyber-weave/</c> or any other file while reporting a plan.
    /// </summary>
    [Fact]
    public void Install_DryRunOnAnEmptyDirectoryCreatesNothing()
    {
        string host = NewDir("install-dry-run");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "kilo",
                Provider = "none",
                DryRun = true,
                Project = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Dry-run", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_files.EnumerateEntries(host));
    }

    /// <summary>
    /// A provider that is not a block mapping cannot be merged. The command reports the merge
    /// refusal and leaves the file alone instead of letting the exception escape as a crash.
    /// </summary>
    [Fact]
    public void Install_FlowProviderValue_FailsWithAnActionableErrorAndLeavesTheConfig()
    {
        const string yaml = "arbiter:\n  enabled: true\n  provider: {kind: kilo, extra: 1}\n";
        string host = NewDir("install-flow-provider");
        string directory = Path.Combine(host, ".kyber-weave");
        _files.CreateDirectory(directory);
        string config = Path.Combine(directory, "kyber-weave.yml");
        _files.WriteAllText(config, yaml);

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = "none", Project = true }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("block mapping", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(yaml, _files.ReadAllText(config));
    }

    /// <summary>
    /// The issue's repro at the command seam: the host config names <c>none</c>, the per-user
    /// override names <c>systemone</c> at the override endpoint, and the key is stored only under
    /// the override origin, so a dry run completes with exit 0 and no key finding.
    /// </summary>
    [Fact]
    public void Install_SystemoneWithOverrideEndpointAndStoredKey_CompletesTheDryRun()
    {
        const string origin = "https://openrouter.ai";
        string host = NewDir("install-override-endpoint");
        WriteOverride("provider:\n  kind: systemone\n  endpoint: https://openrouter.ai/api/v1\n  model: jev-1.14.0\n");
        _store.Seed(origin, "override-key");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "factory",
                Provider = "systemone",
                DryRun = true,
                Project = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.DoesNotContain(ArbiterKeyResolver.MissingKeyCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run with an explicit <c>--config</c> that does not exist still reports the missing
    /// file; only a bare root with no config option is previewed from the YAML a real run would
    /// write.
    /// </summary>
    [Fact]
    public void Install_DryRunWithMissingExplicitConfig_ReportsTheMissingFile()
    {
        string host = NewDir("install-dry-run-missing-config");

        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "kilo",
                Provider = "none",
                DryRun = true,
                Config = Path.Combine("custom", "missing.yml"),
            }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("not found", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_files.EnumerateEntries(host));
    }

    private void WriteProviderConfig(string host, string kind)
    {
        _files.CreateDirectory(Path.Combine(host, ".kyber-weave"));
        _files.WriteAllText(
            ConfigPath(host),
            $"arbiter:\n  enabled: true\n  provider:\n    kind: {kind}\n");
    }

    private void WriteOverride(string yaml)
    {
        string directory = Path.Combine(_home, ".config", "kyber-weave");
        _files.CreateDirectory(directory);
        _files.WriteAllText(Path.Combine(directory, "arbiter.yml"), yaml);
    }

    private static string ConfigPath(string host) =>
        Path.Combine(host, ".kyber-weave", "kyber-weave.yml");

    private string NewDir(string name)
    {
        string dir = Path.Combine(_work, name);
        _files.CreateDirectory(dir);
        return dir;
    }

    private CommandExecution Capture(Func<int> execute)
    {
        int exitCode = execute();
        return new CommandExecution(exitCode, _console.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
