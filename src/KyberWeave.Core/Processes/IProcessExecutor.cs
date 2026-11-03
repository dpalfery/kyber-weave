using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>
///     Injectable boundary over the repository's deadlock-safe process runner.
/// </summary>
/// <remarks>
///     Declared in Core rather than the CLI so the repository doctor, which lives in Core, can take the same
///     seam the CLI fakes use. The namespace is kept so existing references compile unchanged; the production
///     implementation, <c>ProcessExecutor</c>, stays in the CLI.
/// </remarks>
public interface IProcessExecutor
{
    /// <summary>Runs the configured process with the complete standard input.</summary>
    ProcessResult Run(ProcessStartInfo startInfo, string standardInput);
}
