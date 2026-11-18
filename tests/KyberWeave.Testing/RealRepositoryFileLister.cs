using KyberWeave.Data.Hooks;
using KyberWeave.Data.Review;

namespace KyberWeave.Tests;

/// <summary>
/// Builds the real <see cref="FileRepositoryFileLister"/> over the real git runner and file system, for the tests
/// whose subject is a repository on disk. The lister takes its seams, so every test that does not need the disk
/// injects fakes instead.
/// </summary>
public static class RealRepositoryFileLister
{
    /// <summary>The lister over the real process runner and the real file system.</summary>
    public static FileRepositoryFileLister Create() => new(new ProcessProgramRunner(), new PhysicalFileSystem());
}
