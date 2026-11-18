using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Docs.Export;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests;

/// <summary>
/// Builds the real <see cref="DocGraphExporter"/> over the real file system for the tests whose subject is the
/// files it writes. The class takes its write seam, so every test that does not need the disk injects a fake.
/// </summary>
internal static class RealDocGraphExporter
{
    /// <summary>The exporter over <paramref name="resolver"/>, writing to the real file system.</summary>
    /// <param name="resolver">The CodeGraph resolver the export joins against.</param>
    internal static DocGraphExporter Create(ICodeGraphResolver resolver) => new(resolver, new PhysicalFileSystem());
}
