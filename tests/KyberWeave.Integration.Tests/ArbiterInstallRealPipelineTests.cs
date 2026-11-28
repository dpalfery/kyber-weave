using KyberWeave.Tests.Configuration;
using KyberWeave.Data.Configuration;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Arbiter;
using Microsoft.Extensions.DependencyInjection;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// <c>arbiter install</c> through the real Squad deployment: the receipt one global home holds after three targets are
/// installed into it, and one end-to-end render whose trust step the command prints. Both need the real deployment
/// transaction and its receipt file; the command's own decisions are unit tests in
/// <c>KyberWeave.Cli.Tests.ArbiterInstallCommandSeamTests</c>.
/// </summary>
public sealed class ArbiterInstallRealPipelineTests : IDisposable
{
    private const string TypeSafeOrigin = "https://api.typesafe.ai";

    private readonly TempDirectory _temp = new();
    private readonly TempDirectory _home = new();
    private readonly FakeCredentialStore _store = new();
    private readonly ArbiterTestHost _host;

    public ArbiterInstallRealPipelineTests()
    {
        _host = ArbiterTestHost.Create(RegisterFakes);
    }

    public void Dispose()
    {
        _host.Dispose();
        _temp.Dispose();
        _home.Dispose();
    }

    private void RegisterFakes(IServiceCollection services)
    {
        services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(() => _home.Path));
        services.AddSingleton<ICredentialStore>(_store);
        services.AddSingleton<IArbiterKeyInput>(new FakeArbiterKeyInput(() => null, () => null));
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
    public async Task Install_ThreeGlobalTargetsSharingOneHookFileName_AllExitZeroAndOneReceiptHoldsThreeBlocks()
    {
        string host = NewDir("install-three-global-blocks");
        RealUserHostConfigStore.Create().WriteGlobalTargets(_home.Path, ["cursor", "codex", "factory"]);
        ArbiterInstallCommand command = _host.Command<ArbiterInstallCommand>();

        CommandExecution cursor = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "cursor", Provider = "none" },
            CancellationToken.None));
        AssertInstalled(cursor, "cursor");

        CommandExecution codex = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "codex", Provider = "none" },
            CancellationToken.None));
        AssertInstalled(codex, "codex");

        CommandExecution factory = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterInstallSettings { Path = host, Target = "factory", Provider = "none" },
            CancellationToken.None));
        AssertInstalled(factory, "factory");

        string receiptPath = Path.Combine(
            _home.Path,
            ".kyber-weave",
            "squad",
            "global",
            "squad.receipt.json");
        Assert.True(File.Exists(receiptPath), $"Expected one global receipt at '{receiptPath}'.");

        SquadStateStore reader = RealSquadState.Store(new FakeSquadUserPaths(Path.Combine(_temp.Path, "cli-receipt-reader")));
        SquadReceipt receipt = reader.DeserializeReceipt(await File.ReadAllTextAsync(receiptPath));

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

    /// <summary>
    /// End to end through the injected Squad lifecycle seam: a single
    /// <c>arbiter install</c> renders the requested target via the existing Squad
    /// install pipeline and prints that target's trust step verbatim. The render
    /// assertion pins the fake renderer's captured request targets and the
    /// <c>ArbiterHookWiring.TrustSteps</c> seam text, never a live host. One shared
    /// state store backs the whole run, so no mismatched second install is needed.
    /// </summary>
    [Fact]
    public async Task Install_EndToEnd_RendersThroughSquadPipelineAndPrintsTrustStep()
    {
        const string key = "TS-INSTALL-E2E-9f8e7d6c5b4a";
        string host = NewDir("install-e2e");

        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-home-e2e"));
        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        using ArbiterTestHost e2eHost = ArbiterTestHost.Create(services =>
        {
            RegisterFakes(services);
            services.AddSingleton<ISquadUserPaths>(userPaths);
            services.AddSingleton(stateStore);
            services.AddSingleton<ISquadStateStore>(stateStore);
            services.AddSingleton<ISquadReleaseSource>(releaseSource);
            services.AddSingleton<ISquadInstanceReleaseSource>(new ForwardingInstanceReleaseSource(releaseSource));
            services.AddSingleton<ISquadRenderer>(renderer);
            services.AddSingleton(new SquadConsoleOptions(IsInteractive: false));
        });
        ArbiterInstallCommand command = e2eHost.Command<ArbiterInstallCommand>();

        CommandExecution execution = await CaptureAsync(() => command.ExecuteAsync(
            null!,
            new ArbiterInstallSettings
            {
                Path = host,
                Target = "claude",
                Provider = "systemone",
                ArbiterKey = key,
                Yes = true,
                Project = true,
            },
            CancellationToken.None));

        Assert.Equal(0, execution.ExitCode);
        SquadRenderRequest request = Assert.Single(renderer.RenderRequests);
        Assert.Contains(SquadTarget.Claude, request.Targets);
        Assert.Contains(
            ArbiterHookWiring.TrustSteps[SquadTarget.Claude],
            execution.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(key, execution.Output, StringComparison.Ordinal);
        Assert.Equal(key, await _store.ReadAsync(TypeSafeOrigin));
    }

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

    private static async Task<CommandExecution> CaptureAsync(Func<Task<int>> execute)
    {
        CapturedConsoleExecution<int> execution = await ProcessConsoleCapture.RunAsync(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    /// <summary>The instance release source a test points at one fake release source.</summary>
    private sealed class ForwardingInstanceReleaseSource(ISquadReleaseSource inner) : ISquadInstanceReleaseSource
    {
        public Task<SquadReleaseResult> DownloadAndExtractAsync(SquadReleaseRequest request, CancellationToken cancellationToken) =>
            inner.DownloadAndExtractAsync(request, cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
