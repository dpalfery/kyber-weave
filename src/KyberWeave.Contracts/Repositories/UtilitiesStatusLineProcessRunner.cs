using System.Diagnostics;
using KyberWeave.Core.Processes;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Runs a child process to completion, with a bounded wait.</summary>
/// <remarks>
///     A delegate rather than <c>IProcessExecutor</c> so the harness version probe can cap its wait:
///     a harness binary that never answers <c>--version</c> must not hang doctor. Production binds this
///     to <c>ProcessRunner.Run</c> with a short timeout.
/// </remarks>
/// <param name="startInfo">The process to start, with all three streams redirected.</param>
/// <param name="standardInput">The complete standard input; empty for a version probe.</param>
public delegate ProcessResult UtilitiesStatusLineProcessRunner(ProcessStartInfo startInfo, string standardInput);
