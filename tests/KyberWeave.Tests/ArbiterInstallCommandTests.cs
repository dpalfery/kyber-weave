using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T4 contract: <c>kyber-weave arbiter install --target &lt;t&gt;</c> is the one-command
/// install. It validates the target, rejects <c>--global</c>, resolves an empty provider
/// to <c>none</c>, keeps the key in the credential store only, fails visibly when a remote
/// provider has no key, is a no-op on repeat runs, merges config non-destructively, and
/// renders through the existing Squad install pipeline with the trust step printed.
/// RED: <c>ArbiterInstallCommand</c> and <c>ArbiterInstallSettings</c> do not exist yet.
/// The settings shape below mirrors <c>ArbiterUninstallSettings</c> (Target) and
/// <c>ArbiterSettings</c> (Provider, KeyStdin): <c>Target</c>, <c>Provider</c>,
/// <c>ArbiterKey</c> (--arbiter-key), <c>KeyStdin</c>, <c>Global</c>, over the inherited
/// <c>Path</c>.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterInstallCommandTests : IDisposable
{
    private const string TypeSafeOrigin = "https://api.typesafe.ai";

    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _home = new();
    private readonly FakeCredentialStore _store = new();
    private readonly AsyncLocal<bool> _isolated = new();

    private string? _keyStdinValue;
    private string? _keyPromptValue;

    private readonly Func<string> _savedHome;
    private readonly Func<ICredentialStore> _savedStore;
    private readonly Func<string, string?> _savedEnvironment;
    private readonly Func<string?> _savedKeyStdin;
    private readonly Func<string?> _savedKeyPrompt;

    public ArbiterInstallCommandTests()
    {
        _savedHome = ArbiterCommandComposition.HomeDirectory;
        _savedStore = ArbiterCommandComposition.CredentialStore;
        _savedEnvironment = ArbiterCommandComposition.GetEnvironmentVariable;
        _savedKeyStdin = ArbiterCommandComposition.KeyStdinReader;
        _savedKeyPrompt = ArbiterCommandComposition.KeyPrompt;

        ArbiterCommandComposition.HomeDirectory = () => _isolated.Value ? _home.Path : _savedHome();
        ArbiterCommandComposition.CredentialStore = () => _isolated.Value ? _store : _savedStore();
        ArbiterCommandComposition.GetEnvironmentVariable = _ => _isolated.Value ? null : _savedEnvironment(_);
        ArbiterCommandComposition.KeyStdinReader = () => _isolated.Value ? _keyStdinValue : _savedKeyStdin();
        ArbiterCommandComposition.KeyPrompt = () => _isolated.Value ? _keyPromptValue : _savedKeyPrompt();
    }

    public void Dispose()
    {
        ArbiterCommandComposition.HomeDirectory = _savedHome;
        ArbiterCommandComposition.CredentialStore = _savedStore;
        ArbiterCommandComposition.GetEnvironmentVariable = _savedEnvironment;
        ArbiterCommandComposition.KeyStdinReader = _savedKeyStdin;
        ArbiterCommandComposition.KeyPrompt = _savedKeyPrompt;
        _temp.Dispose();
        _home.Dispose();
    }

    /// <summary>
    /// An unknown target is client input, not a crash: exit 2 naming the roster.
    /// </summary>
    [Fact]
    public void Install_InvalidTarget_ExitsTwoNamingTheRoster()
    {
        string host = NewDir("install-bad-target");

        ArbiterInstallCommand command = new();
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
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude", "warp"]);

        ArbiterInstallCommand command = new();
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

        string globalRoot = new SquadGlobalRoots(_ => null, _home.Path).ResolveGlobalRoot(SquadTarget.Claude);
        Assert.True(Directory.Exists(globalRoot), "the explicit global install should write beneath the target's global root");

        string config = File.ReadAllText(UserHostConfig.GetPath(_home.Path));
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

        ArbiterInstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = string.Empty, Project = true }));

        Assert.Equal(0, execution.ExitCode);
        string config = File.ReadAllText(ConfigPath(host));
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

        CommandExecution execution = Capture(() => new ArbiterInstallCommand().Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("kind: systemone", File.ReadAllText(ConfigPath(host)), StringComparison.Ordinal);
        Assert.Contains("systemone", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kept", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>An explicit <c>--provider</c> replaces the configured provider and reports the change.</summary>
    [Fact]
    public void Install_ExplicitProvider_ChangesTheExistingProvider()
    {
        string host = NewDir("install-change-provider");
        WriteProviderConfig(host, "systemone");

        CommandExecution execution = Capture(() => new ArbiterInstallCommand().Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = "none", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        string config = File.ReadAllText(ConfigPath(host));
        Assert.Contains("kind: none", config, StringComparison.Ordinal);
        Assert.DoesNotContain("systemone", config, StringComparison.Ordinal);
        Assert.Contains("changed from systemone", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A machine with nothing configured keeps the default of no provider.</summary>
    [Fact]
    public void Install_OmittedProviderOnAFreshMachine_DefaultsToNone()
    {
        string host = NewDir("install-fresh-provider");

        CommandExecution execution = Capture(() => new ArbiterInstallCommand().Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("kind: none", File.ReadAllText(ConfigPath(host)), StringComparison.Ordinal);
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

        ArbiterInstallCommand command = new();
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
        string config = File.ReadAllText(ConfigPath(host));
        Assert.DoesNotContain(key, config, StringComparison.Ordinal);
        foreach (string file in Directory.GetFiles(host, "*", SearchOption.AllDirectories))
        {
            string content = File.ReadAllText(file);
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

        ArbiterInstallCommand command = new();
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
        ArbiterInstallCommand command = new();

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
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "kyber-weave.yml"),
            "# Operator-owned header\n" +
            "squad:\n" +
            "  targets: [kilo]\n" +
            "# keep this comment\n" +
            "custom-unrelated-key: custom-value\n");

        ArbiterInstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = "none", Project = true }));

        Assert.Equal(0, execution.ExitCode);
        string config = File.ReadAllText(ConfigPath(host));
        Assert.Contains("# Operator-owned header", config, StringComparison.Ordinal);
        Assert.Contains("# keep this comment", config, StringComparison.Ordinal);
        Assert.Contains("custom-unrelated-key: custom-value", config, StringComparison.Ordinal);
        Assert.Contains("targets: [kilo]", config, StringComparison.Ordinal);
        Assert.Contains("none", config, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// End to end through the injected Squad lifecycle seam: a single
    /// <c>arbiter install</c> renders the requested target via the existing Squad
    /// install pipeline and prints that target's trust step verbatim. The render
    /// assertion pins the fake renderer's captured request targets and the
    /// <c>ArbiterHookWiring.TrustSteps</c> seam text, never a live host. One shared
    /// state store backs the whole run, so no mismatched second install is needed.
    /// </summary>
    [Fact]
    public void Install_EndToEnd_RendersThroughSquadPipelineAndPrintsTrustStep()
    {
        const string key = "TS-INSTALL-E2E-9f8e7d6c5b4a";
        string host = NewDir("install-e2e");

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home-e2e"));
        SquadStateStore stateStore = new(userPaths);
        ArbiterInstallCommand command = new(
            userPaths: userPaths,
            stateStore: stateStore,
            releaseSource: releaseSource,
            renderer: renderer,
            isInteractive: false);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "claude",
                Provider = "systemone",
                ArbiterKey = key,
                Yes = true,
                Project = true,
            }));

        Assert.Equal(0, execution.ExitCode);
        SquadRenderRequest request = Assert.Single(renderer.RenderRequests);
        Assert.Contains(SquadTarget.Claude, request.Targets);
        Assert.Contains(
            ArbiterHookWiring.TrustSteps[SquadTarget.Claude],
            execution.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(key, execution.Output, StringComparison.Ordinal);
        Assert.Equal(key, _store.Read(TypeSafeOrigin));
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
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude", "warp"]);

        ArbiterInstallCommand command = new();
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

        string globalRoot = new SquadGlobalRoots(_ => null, _home.Path).ResolveGlobalRoot(SquadTarget.Claude);
        Assert.True(Directory.Exists(globalRoot), "the default install should write beneath the target's global root");

        string config = File.ReadAllText(UserHostConfig.GetPath(_home.Path));
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
        Assert.False(File.Exists(UserHostConfig.GetPath(_home.Path)));
        Assert.False(Directory.Exists(Path.Combine(_home.Path, "docs")));
        Assert.False(File.Exists(Path.Combine(_home.Path, "AGENTS.md")));

        ArbiterInstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "claude",
                Provider = "none",
            }));

        Assert.Equal(0, execution.ExitCode);
        Assert.True(File.Exists(UserHostConfig.GetPath(_home.Path)), "the default global install should create the user config");
        Assert.False(Directory.Exists(Path.Combine(_home.Path, "docs")), "a global install must not scaffold docs under the home directory");
        Assert.False(File.Exists(Path.Combine(_home.Path, "AGENTS.md")), "a global install must not scaffold AGENTS.md under the home directory");
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
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude", "warp", "copilot"]);

        ArbiterInstallCommand command = new();
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

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
        {
            CommandApp app = new();
            app.Configure(config =>
            {
                config.UseStrictParsing();
                config.AddBranch("arbiter", branch => branch.AddCommand<ArbiterInstallCommand>("install"));
            });
            return app.Run(["arbiter", "install", "--target", "claude", "--path", host]);
        });

        Assert.Equal(2, execution.Result);
        Assert.Contains("--project", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--path", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scope flags are mutually exclusive: a global install cannot also opt into project
    /// scope. The command must fail with the install-rule id rather than silently picking one.
    /// </summary>
    [Fact]
    public void Install_GlobalAndProject_ExitsTwoWithKwArbInstall001()
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() =>
        {
            CommandApp app = new();
            app.Configure(config =>
            {
                config.UseStrictParsing();
                config.AddBranch("arbiter", branch => branch.AddCommand<ArbiterInstallCommand>("install"));
            });
            return app.Run(["arbiter", "install", "--target", "claude", "--global", "--project"]);
        });

        Assert.Equal(2, execution.Result);
        Assert.Contains("KW-ARB-INSTALL-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A dry run on a bare root previews instead of installing: it must not scaffold
    /// <c>.kyber-weave/</c> or any other file while reporting a plan.
    /// </summary>
    [Fact]
    public void Install_DryRunOnAnEmptyDirectoryCreatesNothing()
    {
        string host = NewDir("install-dry-run");

        ArbiterInstallCommand command = new();
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
        Assert.Empty(Directory.GetFileSystemEntries(host));
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
        Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "kyber-weave.yml");
        File.WriteAllText(config, yaml);

        ArbiterInstallCommand command = new();
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "kilo", Provider = "none", Project = true }));

        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("block mapping", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(yaml, File.ReadAllText(config));
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

        ArbiterInstallCommand command = new();
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
        Assert.Empty(Directory.GetFileSystemEntries(host));
    }

    /// <summary>
    /// Three block-bearing targets installed in turn into one home all exit 0, and the single
    /// global receipt records one <c>hooks.json</c> block per target. This is the reported
    /// defect: the second install exited 1 with "Squad receipt block path 'hooks.json' for target
    /// 'cursor' is not a unique portable deployment path." and rolled back, so one home could
    /// hold only one of cursor, codex, factory and antigravity. Each install prepends the target
    /// it just wrote, so the blocks are stored in the reverse of installation order; the contract
    /// is the set of blocks one receipt holds, never their sequence, and a target recorded twice
    /// has to fail rather than collapse into that set.
    /// </summary>
    [Fact]
    public void Install_ThreeGlobalTargetsSharingOneHookFileName_AllExitZeroAndOneReceiptHoldsThreeBlocks()
    {
        string host = NewDir("install-three-global-blocks");
        UserHostConfig.WriteGlobalTargets(_home.Path, ["cursor", "codex", "factory"]);
        ArbiterInstallCommand command = new();

        CommandExecution cursor = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "cursor", Provider = "none" }));
        AssertInstalled(cursor, "cursor");

        CommandExecution codex = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "codex", Provider = "none" }));
        AssertInstalled(codex, "codex");

        CommandExecution factory = Capture(() => command.Execute(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "factory", Provider = "none" }));
        AssertInstalled(factory, "factory");

        string receiptPath = Path.Combine(
            _home.Path,
            ".kyber-weave",
            "squad",
            "global",
            "squad.receipt.json");
        Assert.True(File.Exists(receiptPath), $"Expected one global receipt at '{receiptPath}'.");

        SquadStateStore reader = new(new FakeSquadUserPaths(Path.Combine(_temp.Path, "cli-receipt-reader")));
        SquadReceipt receipt = reader.DeserializeReceipt(File.ReadAllText(receiptPath));

        Assert.Equal(SquadDeploymentScope.Global, receipt.Scope);
        Assert.Equal(3, receipt.Blocks.Count);

        Dictionary<string, string> pathByTarget = new(StringComparer.Ordinal);
        Assert.All(
            receipt.Blocks,
            block => Assert.True(
                pathByTarget.TryAdd(block.Target, block.RelativePath),
                $"The receipt holds more than one block for target '{block.Target}'."));

        string[] expectedTargets = ["codex", "cursor", "factory"];
        Assert.Equal(expectedTargets, pathByTarget.Keys.Order(StringComparer.Ordinal));
        Assert.All(pathByTarget.Values, path => Assert.Equal("hooks.json", path));
    }

    private static void WriteProviderConfig(string host, string kind)
    {
        string directory = Path.Combine(host, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "kyber-weave.yml"),
            $"arbiter:\n  enabled: true\n  provider:\n    kind: {kind}\n");
    }

    private static string ConfigPath(string host) =>
        Path.Combine(host, ".kyber-weave", "kyber-weave.yml");

    private static void AssertInstalled(CommandExecution execution, string target) =>
        Assert.True(
            execution.ExitCode == 0,
            $"The global '{target}' install exited {execution.ExitCode}: {execution.Output}");

    private string NewDir(string name)
    {
        string dir = Path.Combine(_temp.Path, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private CommandExecution Capture(Func<int> execute)
    {
        _isolated.Value = true;
        try
        {
            CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
            return new CommandExecution(execution.Result, execution.Output);
        }
        finally
        {
            _isolated.Value = false;
        }
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

        public List<(string Origin, string Key)> Writes { get; } = [];

        public string? Read(string origin)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            return Entries.TryGetValue(origin, out string? key) ? key : null;
        }

        public void Write(string origin, string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            ArgumentException.ThrowIfNullOrEmpty(key);
            Writes.Add((origin, key));
            Entries[origin] = key;
        }

        public void Delete(string origin)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(origin);
            Entries.Remove(origin);
        }
    }
}
