using KyberWeave.Core.Arbiter.Stores;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Opens the Arbiter's append-only state files under an arbiter directory.</summary>
/// <remarks>The directory varies per repository and per hook scope, so the files are opened per use rather than registered once.</remarks>
public interface IArbiterStateFactory
{
    /// <summary>Opens the in-flight ledger.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    IArbiterLedger OpenLedger(string arbiterDirectory);

    /// <summary>Opens the decision log.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    IArbiterDecisionLog OpenDecisionLog(string arbiterDirectory);

    /// <summary>Opens the review, failure and return stores.</summary>
    /// <param name="arbiterDirectory">The arbiter state directory.</param>
    /// <param name="options">The size and retention limits; the shipped defaults when null.</param>
    /// <param name="clock">The clock retention is measured against; the system clock when null.</param>
    IArbiterRunStore OpenRunStore(string arbiterDirectory, ArbiterStoreOptions? options = null, TimeProvider? clock = null);
}
