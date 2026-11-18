using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Data.Docs.FileSystem;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Builds the real <see cref="GateReportFacts"/> over the real repository file store for the tests whose subject is
/// the gate report a run wrote to disk. The class itself takes its seam, so every test that does not need the disk
/// injects the in-memory store instead.
/// </summary>
internal static class RealGateReportFacts
{
    /// <summary>The gate report reader over the real repository file store, path resolver and environment.</summary>
    internal static GateReportFacts Create() =>
        new(
            new PhysicalRepositoryFileStore(),
            new PhysicalPathResolver(new PhysicalFileSystem()),
            new ProcessEnvironment());
}
