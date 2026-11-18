using KyberWeave.Data.Configuration;
using KyberWeave.Data.Hooks;

namespace KyberWeave.Tests.Configuration;

/// <summary>Builds the user host-config store over the real file system, for the tests that arrange a home on disk.</summary>
internal static class RealUserHostConfigStore
{
    /// <summary>The store over the real file system.</summary>
    internal static FileUserHostConfigStore Create() => new(new PhysicalFileSystem());
}
