using KyberWeave.Testing.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>
///     Verifies how a stored project selection reaches a real Squad install.
///     Acceptance criteria:
///     - 6.3: A project selection SHALL NOT change a global install.
/// </summary>
public sealed class SquadModelPresetSelectionLifecycleTests
{
    private const string FastPresetYaml = """
                                          schema: kyber-squad.model-preset/v1
                                          profiles:
                                            general:
                                              default: gpt-4o-mini
                                              copilot: gpt-4o-mini
                                          """;

    private readonly InMemorySquadState _state = new();

    [Fact]
    public async Task GlobalInstallIgnoresProjectSelection()
    {
        // Arrange (AC 6.3)
        string projectRoot = Path.Combine(_state.Files.Root, "work", "global-install-project");
        _state.Files.CreateDirectory(projectRoot);
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadModelPresetSelectionStore selectionStore = new(userPaths, _state.Files, _state.Semantics);
        selectionStore.Set(projectRoot, SquadTarget.Copilot, "fast");

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        _state.Files.CreateDirectory(presetsDir);
        _state.Files.WriteAllText(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml);

        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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
        string projectRoot = Path.Combine(_state.Files.Root, "work", "project-install-selection");
        _state.Files.CreateDirectory(projectRoot);
        ISquadUserPaths userPaths = _state.UserPaths;

        SquadModelPresetSelectionStore selectionStore = new(userPaths, _state.Files, _state.Semantics);
        selectionStore.Set(projectRoot, SquadTarget.Copilot, "fast");

        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        _state.Files.CreateDirectory(presetsDir);
        _state.Files.WriteAllText(Path.Combine(presetsDir, "fast.yml"), FastPresetYaml);

        using FakeSquadReleaseSource releaseSource = ReleaseSource();
        FakeSquadRenderer renderer = new();
        SquadLifecycleService service = SquadTestCommands.InMemoryLifecycle(_state, releaseSource, renderer);

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

    private FakeSquadReleaseSource ReleaseSource()
    {
        string source = Path.Combine(_state.Files.Root, "release", "kyber-squad");
        _ = CanonicalSquadTree.CopyInto(_state.Files, source);
        return FakeSquadReleaseSource.FromDirectory(source, _state.Files);
    }
}
