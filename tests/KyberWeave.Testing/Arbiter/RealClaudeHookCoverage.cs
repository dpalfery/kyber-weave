using KyberWeave.Core.Arbiter;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the real <see cref="ClaudeHookCoverage"/> over the real file system for the tests whose subject is how a
/// failing read on disk is handled. The class itself takes its seam, so every other test injects the in-memory file
/// system instead.
/// </summary>
internal static class RealClaudeHookCoverage
{
    /// <summary>The hook coverage check over the real file system.</summary>
    internal static ClaudeHookCoverage Create() => new(new PhysicalFileSystem());
}
