using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the real <see cref="FileTextFacts"/> over the real repository file store for the tests whose subject is
/// what a file on disk holds. The class itself takes its seam, so every test that does not need the disk injects the
/// in-memory store instead.
/// </summary>
internal static class RealFileTextFacts
{
    /// <summary>The excerpt reader over the real repository file store, path resolver and environment.</summary>
    internal static FileTextFacts Create() =>
        new(
            new PhysicalRepositoryFileStore(),
            new PhysicalPathResolver(new PhysicalFileSystem()),
            new ProcessEnvironment());
}
