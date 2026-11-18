using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Creates deployment transactions over the state store.</summary>
/// <param name="stateStore">The state store the transactions journal in.</param>
/// <param name="files">The file system the transactions read and write through.</param>
/// <param name="semantics">The path rules the transactions resolve deployment paths with.</param>
/// <param name="lease">The lease that serialises deployments of one root.</param>
/// <param name="ids">Names each transaction and its temporary files.</param>
public sealed class SquadTransactionFactory(
    ISquadStateStore stateStore,
    IWritableFileSystem files,
    IFileSystemPathSemantics semantics,
    ILease lease,
    IIdGenerator ids) : ISquadTransactionFactory
{
    /// <inheritdoc />
    public ISquadTransaction Create(ISquadTransactionObserver? observer) =>
        new SquadTransaction(stateStore, files, semantics, lease, ids, observer);
}
