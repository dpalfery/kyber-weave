using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Unit twin of <c>SquadDeployTimeGuidanceWiringTests</c>' unreadable-entry cases: an update whose deployed entry
/// file cannot be read succeeds with a warning that names the file and only its owning target, on a real run and on a
/// dry run. The file system throws on the read, so no permission change or disk is involved.
/// </summary>
public sealed class SquadUnreadableDeployedEntryUnitTests
{
    private static readonly SquadTarget[] Targets = [SquadTarget.Codex, SquadTarget.Claude];

    private readonly InMemorySquadState _state = new();

    /// <summary>An unreadable entry becomes a warning naming its path and owning target; the update still succeeds.</summary>
    [Fact]
    public async Task UpdateWithAnUnreadableDeployedEntryDegradesToAWarningNamingTheOwningTargetOnly()
    {
        (SquadLifecycleService lifecycle, string root) = await InstallAsync();
        FailReadsOfConductor();

        SquadLifecycleResult update = await lifecycle.UpdateAsync(
            new SquadUpdateRequest(root, SquadDeploymentScope.Project, Targets, Version: "1.2.4"));

        Assert.True(update.Success, string.Join("; ", update.Errors ?? []));
        string warning = Assert.Single(
            update.Warnings ?? [],
            line => line.StartsWith("Could not read deployed file", StringComparison.Ordinal));
        Assert.Contains("conductor.toml", warning, StringComparison.Ordinal);
        Assert.Contains("for target 'codex'", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("claude", Regex.Replace(warning, "'[^']*'", "''"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A dry run reports the same unreadable-entry warning and still succeeds.</summary>
    [Fact]
    public async Task DryRunUpdateWithAnUnreadableDeployedEntrySurfacesTheWarning()
    {
        (SquadLifecycleService lifecycle, string root) = await InstallAsync();
        FailReadsOfConductor();

        SquadLifecycleResult dryRun = await lifecycle.UpdateAsync(
            new SquadUpdateRequest(root, SquadDeploymentScope.Project, Targets, Version: "1.2.4", DryRun: true));

        Assert.True(dryRun.Success, string.Join("; ", dryRun.Errors ?? []));
        Assert.True(dryRun.DryRun);
        Assert.Contains(
            dryRun.Warnings ?? [],
            warning => warning.StartsWith("Could not read deployed file", StringComparison.Ordinal)
                       && warning.Contains("conductor.toml", StringComparison.Ordinal));
    }

    private async Task<(SquadLifecycleService Lifecycle, string Root)> InstallAsync()
    {
        string root = _state.CreateRoot("unreadable-entry");
        FakeSquadReleaseSource release = new() { Files = _state.Files };
        FakeSquadRenderer renderer = FakeSquadRenderer.Responding(request => new SquadRenderResult(
            true,
            [.. request.Targets.Select(Conductor)],
            [],
            [],
            []));
        SquadLifecycleService lifecycle = SquadTestCommands.InMemoryLifecycle(_state, release, renderer);

        SquadLifecycleResult install = await lifecycle.InstallAsync(
            new SquadInstallRequest(root, SquadDeploymentScope.Project, Targets, Version: "1.2.3"));
        Assert.True(install.Success, string.Join("; ", install.Errors ?? []));
        return (lifecycle, root);
    }

    private void FailReadsOfConductor() =>
        _state.Files.FailWhen(
            operation => operation.Path is { } path
                         && path.Contains("conductor.toml", StringComparison.Ordinal)
                         && operation.Name.StartsWith("Read", StringComparison.Ordinal),
            new UnauthorizedAccessException("Access to the path is denied."));

    private static SquadDeploymentFile Conductor(SquadTarget target)
    {
        string token = SquadTargetCatalog.GetToken(target);
        string extension = target == SquadTarget.Codex ? "toml" : "md";
        return new SquadDeploymentFile($".{token}/agents/conductor.{extension}", Encoding.UTF8.GetBytes("conductor\n"), token);
    }
}
