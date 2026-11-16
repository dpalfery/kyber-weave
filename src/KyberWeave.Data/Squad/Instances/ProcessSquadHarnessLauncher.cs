using System.Diagnostics;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Builds harness start information through <see cref="SquadHarnessLauncher"/> and starts the harness on the console.</summary>
public sealed class ProcessSquadHarnessLauncher : ISquadHarnessLauncher
{
    /// <inheritdoc />
    public ProcessStartInfo PrepareLaunch(
        SquadInstanceRow row,
        string instanceRoot,
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        Func<string, string?> findOnPath,
        bool isWindows) =>
        SquadHarnessLauncher.BuildStartInfo(row, instanceRoot, args, getEnv, findOnPath, isWindows);

    /// <inheritdoc />
    public string? FindOnPath(string executableName, Func<string, string?>? getEnv = null) =>
        SquadHarnessLauncher.FindOnPath(executableName, getEnv);

    /// <inheritdoc />
    public int RunInteractive(ProcessStartInfo startInfo) => ProcessRunner.RunInteractive(startInfo);
}
