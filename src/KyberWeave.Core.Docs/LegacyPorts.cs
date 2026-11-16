using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Docs.FileSystem;

namespace KyberWeave.Core.Docs;

/// <summary>
/// The physical adapters the classes still in this assembly hand to the ones that moved. Each of those classes goes
/// when it moves to a layer and takes the ports from its constructor instead.
/// </summary>
internal static class LegacyPorts
{
    internal static readonly IRepositoryFileStore Files = new PhysicalRepositoryFileStore();
}
