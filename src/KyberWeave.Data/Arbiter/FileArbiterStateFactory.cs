using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Stores;

namespace KyberWeave.Data.Arbiter;

/// <summary>Opens the Arbiter's ledger, decision log and run stores as files under the arbiter directory.</summary>
public sealed class FileArbiterStateFactory : IArbiterStateFactory
{
    /// <inheritdoc />
    public IArbiterLedger OpenLedger(string arbiterDirectory) => new InFlightLedger(arbiterDirectory);

    /// <inheritdoc />
    public IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory) => new DecisionLog(arbiterDirectory);

    /// <inheritdoc />
    public IArbiterRunStore OpenRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, Func<DateTimeOffset>? clock = null) =>
        new ArbiterRunStore(arbiterDirectory, options, clock);
}
