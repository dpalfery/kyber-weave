using KyberWeave.Data.Configuration;
using KyberWeave.Data.Host;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Data.Review;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// Builds the shipped configuration repository over the real disk, for a test that arranges a fixture there and then
/// asks what the product reads from it. A test of behaviour takes <c>FakeKyberWeaveConfigRepository</c> instead.
/// </summary>
public static class RealFileKyberWeaveConfigRepository
{
    /// <summary>The repository over the real file system, paths and process environment.</summary>
    public static FileKyberWeaveConfigRepository Create()
    {
        PhysicalFileSystem files = new();
        return new FileKyberWeaveConfigRepository(
            new FileRepositoryTreeProbe(files),
            files,
            new PhysicalPathResolver(files),
            new ProcessEnvironment());
    }
}
