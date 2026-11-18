using KyberWeave.Testing.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
///     Verifies how a stored project selection reaches a real Squad install.
///     Acceptance criteria:
///     - 6.3: A project selection SHALL NOT change a global install.
/// </summary>
public sealed class SquadModelPresetSelectionLifecycleTests : IDisposable
{
    private const string FastPresetYaml = """
                                          schema: kyber-squad.model-preset/v1
                                          profiles:
                                            general:
                                              default: gpt-4o-mini
                                              copilot: gpt-4o-mini
                                          """;

    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public async Task GlobalInstallIgnoresProjectSelection()
    {
        // Arrange (AC 6.3)
        string projectRoot = Path.Combine(_temp.Path, "global-install-project");
        Directory.CreateDirectory(projectRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));

        SquadModelPresetSelectionStore selectionStore = RealSquadSelectionStore.Create(userPaths);
        selectionStore.Set(projectRoot, SquadTarget.Copilot, "fast");

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        await File.WriteAllTextAsync(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml);

        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(releaseSource, renderer, stateStore, selectionStore: selectionStore);

        SquadInstallRequest request = new(
            projectRoot,
            SquadDeploymentScope.Global,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: null);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Single(renderer.RenderRequests);
        Assert.Null(renderer.RenderRequests[0].ModelPresets);
    }

    [Fact]
    public async Task ProjectInstallWithNullModelPresetsAppliesStoredProjectSelection()
    {
        // Arrange (AC 6.3)
        string projectRoot = Path.Combine(_temp.Path, "project-install-selection");
        Directory.CreateDirectory(projectRoot);
        FakeSquadUserPaths userPaths = new(Path.Combine(_temp.Path, "user-data"));

        SquadModelPresetSelectionStore selectionStore = RealSquadSelectionStore.Create(userPaths);
        selectionStore.Set(projectRoot, SquadTarget.Copilot, "fast");

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);
        await File.WriteAllTextAsync(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml);

        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        using FakeSquadReleaseSource releaseSource = new();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadHostDefaults.CreateLifecycleService(releaseSource, renderer, stateStore, selectionStore: selectionStore);

        SquadInstallRequest request = new(
            projectRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Copilot],
            Version: "1.0.0",
            ModelPresets: null);

        // Act
        SquadLifecycleResult result = await service.InstallAsync(request);

        // Assert
        Assert.True(result.Success);
        Assert.Single(renderer.RenderRequests);
        IReadOnlyDictionary<SquadTarget, SquadModelPreset>? appliedPresets = renderer.RenderRequests[0].ModelPresets;
        Assert.NotNull(appliedPresets);
        Assert.True(appliedPresets.ContainsKey(SquadTarget.Copilot));
        Assert.Equal("fast", appliedPresets[SquadTarget.Copilot].Name);
    }
}
