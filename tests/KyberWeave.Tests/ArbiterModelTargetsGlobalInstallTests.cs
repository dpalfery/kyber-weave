using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A user-scope (<c>squad install --global</c>) install and <c>arbiter doctor</c> must agree on where each
/// harness's model-targets table lives. The doctor reads the table the install wrote, so a clean install
/// followed by the doctor check reports no <c>KW-ARB-MODEL-001</c>.
/// </summary>
public sealed class ArbiterModelTargetsGlobalInstallTests : IDisposable
{
    private const string ShadowModelTargetingYaml =
        "arbiter:\n" +
        "  enabled: true\n" +
        "  model-targeting:\n" +
        "    mode: shadow\n";

    private readonly TempDirectory _temp = new();
    private readonly ArbiterTestHost _host;
    private readonly string _applicationData;
    private readonly string _home;
    private readonly string _repo;

    public ArbiterModelTargetsGlobalInstallTests()
    {
        _applicationData = Path.Combine(_temp.Path, "app-data");
        _home = Path.Combine(_applicationData, "home");
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_repo);
        _host = ArbiterTestHost.Create(services =>
            services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(() => _home)));
    }

    public void Dispose()
    {
        _host.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task UserScopeCopilotInstall_ThenDoctor_ReportsNoMissingModelTargets()
    {
        SquadLifecycleResult result = await InstallGlobalAsync(SquadTarget.Copilot);
        Assert.True(result.Success, string.Join("; ", result.Warnings ?? []));

        new FileUserHostConfigStore().WriteGlobalTargets(_home, ["copilot"]);
        WriteProjectConfig();

        string output = RunDoctor();

        Assert.DoesNotContain("KW-ARB-MODEL-001", output, StringComparison.Ordinal);
    }

    private async Task<SquadLifecycleResult> InstallGlobalAsync(SquadTarget target)
    {
        SquadGlobalRoots globalRoots = new(_ => null, _home);
        SquadStateStore store = new(new FakeSquadUserPaths(_applicationData));
        using CorpusSquadReleaseSource release = new();
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(
            release,
            SquadHostDefaults.ResolveRenderer(),
            store,
            globalRoots: globalRoots);

        return await service.InstallAsync(new SquadInstallRequest(
            KyberWeaveTestPaths.ToolRoot,
            SquadDeploymentScope.Global,
            [target],
            Version: "1.2.3",
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 45)));
    }

    private void WriteProjectConfig()
    {
        string directory = Path.Combine(_repo, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), ShadowModelTargetingYaml);
    }

    private string RunDoctor() =>
        ProcessConsoleCapture.Run(() => _host.Command<ArbiterDoctorCommand>().Execute(
            null!,
            new ArbiterSettings { Path = _repo })).Output;

}
