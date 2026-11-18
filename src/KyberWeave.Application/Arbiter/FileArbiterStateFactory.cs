using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;

namespace KyberWeave.Data.Arbiter;

/// <summary>Opens the Arbiter's ledger, decision log and run stores as files under the arbiter directory.</summary>
/// <param name="log">The append and windowed-read mechanics the log files share.</param>
/// <param name="files">The file system the run stores list and age their siblings through.</param>
/// <param name="writes">The file system the run stores delete expired siblings through.</param>
public sealed class FileArbiterStateFactory(
    IArbiterLogStore log,
    IFileSystem files,
    IWritableFileSystem writes) : IArbiterStateFactory
{
    /// <inheritdoc />
    public IArbiterLedger OpenLedger(string arbiterDirectory) => new InFlightLedger(arbiterDirectory, log);

    /// <inheritdoc />
    public IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory) => new DecisionLog(arbiterDirectory, log);

    /// <inheritdoc />
    public IArbiterRunStore OpenRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, TimeProvider? clock = null) =>
        new ArbiterRunStore(arbiterDirectory, log, files, writes, options, clock);
}
