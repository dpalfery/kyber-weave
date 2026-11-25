using KyberWeave.Contracts.Repositories;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Application.Tests.Squad;

/// <summary>Runs the Squad file seam's write contract against the in-memory fake.</summary>
public sealed class InMemorySquadFileAccessWriteContractTests : SquadFileAccessWriteContract
{
    /// <inheritdoc />
    protected override ISquadFileAccess CreateFileAccess() => new InMemorySquadFileAccess(new InMemoryFileSystem());
}
