using KyberWeave.Contracts.Repositories;
using System.Diagnostics;
using KyberWeave.Cli.Commands.Utilities;
using KyberWeave.Core.Processes;

namespace KyberWeave.Data.Utilities;

/// <summary>The production process runner of the status-line doctor, with the harness version probe's bounded wait.</summary>
public static class StatusLineVersionProbeRunner
{
    /// <summary>How long a harness version probe may run before doctor gives up on it.</summary>
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The runner the container hands out when a host or test has not registered its own.</summary>
    public static UtilitiesStatusLineProcessRunner Create()
    {
        return static (startInfo, standardInput) => ProcessRunner.Run(startInfo, standardInput, VersionProbeTimeout);
    }
}
