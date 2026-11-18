using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Configuration;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Release;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Data.Configuration;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A user-scope (<c>squad install --global</c>) install, the <c>squad update --global</c> that follows it, and
/// <c>arbiter doctor</c> must agree on the model-targets table each harness's global hook reads. The doctor
/// reads the table the deployment wrote, so an install and then an update report no <c>KW-ARB-MODEL-001</c>,
/// whether the update rewrote the table or left byte-identical content alone.
/// </summary>
public sealed class ArbiterModelTargetsGlobalInstallTests : IDisposable
{
    private const string ShadowModelTargetingYaml =
        "arbiter:\n" +
        "  enabled: true\n" +
        "  model-targeting:\n" +
        "    mode: shadow\n";

    private const string FastPresetYaml = """
                                          schema: kyber-squad.model-preset/v1
                                          profiles:
                                            deep-planning:
                                              default: inherit
                                              copilot: gpt-4o
                                            general:
                                              default: gpt-4o-mini
                                              copilot: gpt-4o-mini
                                          """;

    private const string QualityPresetYaml = """
                                             schema: kyber-squad.model-preset/v1
                                             profiles:
                                               deep-planning:
                                                 default: o3-mini
                                                 copilot: o3-mini
                                               general:
                                                 default: gpt-4o
                                                 copilot: gpt-4o
                                             """;

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

        RealUserHostConfigStore.Create().WriteGlobalTargets(_home, ["copilot"]);
        WriteProjectConfig();

        string output = RunDoctor();

        Assert.DoesNotContain("KW-ARB-MODEL-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An update that renders the same table does not rewrite the file, so its write time stays at the
    /// install's while the lock moves on. The doctor must still report the table as current: a byte-identical
    /// table is the table the last update wrote, and telling the operator to run the same update again
    /// would never clear the warning.
    /// </summary>
    [Fact]
    public async Task UserScopeCopilotUpdateWithUnchangedContent_Doctor_ReportsNoMissingModelTargets()
    {
        SquadLifecycleResult install = await InstallGlobalAsync(SquadTarget.Copilot);
        Assert.True(install.Success, string.Join("; ", install.Warnings ?? []));

        RealUserHostConfigStore.Create().WriteGlobalTargets(_home, ["copilot"]);
        WriteProjectConfig();

        string targetsPath = GlobalTargetsPath(SquadTarget.Copilot);
        byte[] installedContent = await File.ReadAllBytesAsync(targetsPath);
        DateTime installedAt = DateTime.UtcNow.AddHours(-2);
        File.SetLastWriteTimeUtc(targetsPath, installedAt);

        SquadLifecycleResult update = await UpdateGlobalAsync(SquadTarget.Copilot);
        Assert.True(update.Success, string.Join("; ", update.Warnings ?? []));

        Assert.Equal(installedContent, await File.ReadAllBytesAsync(targetsPath));
        Assert.Equal(installedAt, File.GetLastWriteTimeUtc(targetsPath));

        string output = RunDoctor();

        Assert.DoesNotContain("KW-ARB-MODEL-001", output, StringComparison.Ordinal);
    }

    /// <summary>The other half of the contract: a new preset changes the table, and the update rewrites it.</summary>
    [Fact]
    public async Task UserScopeCopilotUpdateWithAChangedPreset_RewritesTheModelTargetsFile()
    {
        await WritePresetAsync("fast", FastPresetYaml);
        await WritePresetAsync("quality", QualityPresetYaml);

        SquadLifecycleResult install = await InstallGlobalAsync(SquadTarget.Copilot, "fast");
        Assert.True(install.Success, string.Join("; ", install.Warnings ?? []));

        RealUserHostConfigStore.Create().WriteGlobalTargets(_home, ["copilot"]);
        WriteProjectConfig();

        string targetsPath = GlobalTargetsPath(SquadTarget.Copilot);
        byte[] installedContent = await File.ReadAllBytesAsync(targetsPath);

        SquadLifecycleResult update = await UpdateGlobalAsync(SquadTarget.Copilot, "quality");
        Assert.True(update.Success, string.Join("; ", update.Warnings ?? []));

        byte[] updatedContent = await File.ReadAllBytesAsync(targetsPath);
        Assert.NotEqual(installedContent, updatedContent);
        Assert.Contains("o3-mini", System.Text.Encoding.UTF8.GetString(updatedContent), StringComparison.Ordinal);

        string output = RunDoctor();

        Assert.DoesNotContain("KW-ARB-MODEL-001", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table whose bytes are not the ones the deployment recorded cannot be trusted: it may name models
    /// the presets do not. The receipt's digest is what proves it, not the file's write time.
    /// </summary>
    [Fact]
    public async Task UserScopeCopilotTableEditedAfterInstall_Doctor_ReportsMissingModelTargets()
    {
        SquadLifecycleResult install = await InstallGlobalAsync(SquadTarget.Copilot);
        Assert.True(install.Success, string.Join("; ", install.Warnings ?? []));

        RealUserHostConfigStore.Create().WriteGlobalTargets(_home, ["copilot"]);
        WriteProjectConfig();

        string targetsPath = GlobalTargetsPath(SquadTarget.Copilot);
        string edited = (await File.ReadAllTextAsync(targetsPath)).Replace(
            "\"bundle-digest\": \"",
            "\"bundle-digest\": \"edited-",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(targetsPath, edited);

        string output = RunDoctor();

        Assert.Contains("KW-ARB-MODEL-001", output, StringComparison.Ordinal);
    }

    private async Task<SquadLifecycleResult> InstallGlobalAsync(
        SquadTarget target,
        string? presetName = null)
    {
        using CorpusSquadReleaseSource release = new();
        SquadLifecycleService service = CreateLifecycleService(release);

        return await service.InstallAsync(new SquadInstallRequest(
            CheckoutCorpusSource.Checkout.Root,
            SquadDeploymentScope.Global,
            [target],
            Version: "1.2.3",
            ModelPresets: Presets(target, presetName),
            Arbiter: Wiring));
    }

    private async Task<SquadLifecycleResult> UpdateGlobalAsync(
        SquadTarget target,
        string? presetName = null)
    {
        using CorpusSquadReleaseSource release = new();
        SquadLifecycleService service = CreateLifecycleService(release);

        return await service.UpdateAsync(new SquadUpdateRequest(
            CheckoutCorpusSource.Checkout.Root,
            SquadDeploymentScope.Global,
            [target],
            Version: "1.2.3",
            ModelPresets: Presets(target, presetName),
            Arbiter: Wiring));
    }

    private static SquadArbiterWiring Wiring => new(Enabled: true, HookTimeoutSeconds: 45);

    private static IReadOnlyDictionary<SquadTarget, string>? Presets(SquadTarget target, string? presetName) =>
        presetName is null
            ? null
            : new Dictionary<SquadTarget, string> { [target] = presetName };

    private SquadLifecycleService CreateLifecycleService(ISquadReleaseSource release) =>
        SquadHostDefaults.CreateLifecycleService(
            release,
            SquadHostDefaults.ResolveRenderer(),
            RealSquadState.Store(new FakeSquadUserPaths(_applicationData)),
            globalRoots: new SquadGlobalRoots(_ => null, _home));

    private string GlobalTargetsPath(SquadTarget target) =>
        Path.Combine(
            new SquadGlobalRoots(_ => null, _home).ResolveGlobalRoot(target),
            ".kyber-weave",
            "arbiter",
            $"model-targets.{SquadTargetCatalog.GetToken(target)}.json");

    private async Task WritePresetAsync(string name, string yaml)
    {
        string directory = Path.Combine(_home, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, $"{name}.yml"), yaml);
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
