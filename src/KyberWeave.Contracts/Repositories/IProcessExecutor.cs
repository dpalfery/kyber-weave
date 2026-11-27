using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Contracts.Repositories;

/// <summary>
///     Injectable boundary over the repository's deadlock-safe process runner.
/// </summary>
/// <remarks>
///     One role view over the process seam. The namespace is kept so existing references compile unchanged while
///     the callers move to <see cref="KyberWeave.Contracts.Repositories.IProcessRunner"/>.
/// </remarks>
public interface IProcessExecutor
{
    /// <summary>Runs the configured process with the complete standard input.</summary>
    ProcessResult Run(ProcessStartInfo startInfo, string standardInput);
}
