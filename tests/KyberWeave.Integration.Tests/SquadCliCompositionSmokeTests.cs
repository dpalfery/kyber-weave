using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The Squad verbs' default collaborators are the host's own physical adapters and the real home and
/// application-data directories, so this stays a smoke test of that composition. Each command's behaviour is
/// unit-tested over the in-memory seams instead (<c>SquadInstallAndUpdateCommandInMemoryTests</c>,
/// <c>SquadStatusCommandInMemoryTests</c>, <c>SquadUninstallCommandInMemoryTests</c>,
/// <c>SquadDoctorCommandInMemoryTests</c> and <c>SquadPackCommandSourceTests</c> in KyberWeave.Cli.Tests).
/// </summary>
public sealed class SquadCliCompositionSmokeTests
{
    [Fact]
    public void SquadCommandsDefaultCollaboratorsInstantiateOverTheRealHost()
    {
        SquadInstallCommand install = SquadTestCommands.Install();
        SquadUpdateCommand update = SquadTestCommands.Update();
        SquadUninstallCommand uninstall = SquadTestCommands.Uninstall();
        SquadStatusCommand status = SquadTestCommands.Status();
        SquadDoctorCommand doctor = SquadTestCommands.Doctor();
        SquadPackCommand pack = SquadTestCommands.Pack();

        Assert.NotNull(install);
        Assert.NotNull(update);
        Assert.NotNull(uninstall);
        Assert.NotNull(status);
        Assert.NotNull(doctor);
        Assert.NotNull(pack);
    }
}
