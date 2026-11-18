using KyberWeave.Data.Hooks;
using KyberWeave.Data.Review;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// Builds the real <see cref="FileRepositoryTreeProbe"/> over the real file system, for the tests whose subject is a
/// repository on disk. The probe takes its seam, so every test that does not need the disk injects a fake instead.
/// </summary>
public static class RealRepositoryTreeProbe
{
    /// <summary>The tree probe over the real file system.</summary>
    public static FileRepositoryTreeProbe Create() => new(new PhysicalFileSystem());
}
