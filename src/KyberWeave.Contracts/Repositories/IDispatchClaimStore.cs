using KyberWeave.Arbiter.Hooks;

namespace KyberWeave.Contracts.Repositories;

/// <summary>
/// The one-evaluation-per-dispatch claim store. The project-scope hook and the user-level hook can both see the same
/// dispatch; whichever creates the claim first evaluates it, and the other allows or defers without writing a second decision.
/// </summary>
public interface IDispatchClaimStore
{
    /// <summary>Takes the claim for <paramref name="key"/>.</summary>
    /// <param name="projectRoot">The repository root whose <c>artifacts/arbiter/claims</c> folder holds the claim.</param>
    /// <param name="key">The dispatch event being claimed.</param>
    /// <param name="owner">The owner line written into the claim: <see cref="DispatchClaimOwners.Project"/> or <see cref="DispatchClaimOwners.Global"/>.</param>
    /// <returns>True when this caller created the claim; false when a claim already exists.</returns>
    bool TryClaim(string projectRoot, DispatchClaimKey key, string owner);

    /// <summary>Reads the owner line of an existing claim.</summary>
    /// <param name="projectRoot">The repository root that holds the claim.</param>
    /// <param name="key">The dispatch event.</param>
    /// <returns>The owner, or null when there is no claim.</returns>
    string? ReadOwner(string projectRoot, DispatchClaimKey key);

    /// <summary>Waits, polling asynchronously, for a claim to appear for <paramref name="key"/>.</summary>
    /// <param name="projectRoot">The repository root that holds the claim.</param>
    /// <param name="key">The dispatch event.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>True as soon as a claim exists; false when the wait window closes first.</returns>
    Task<bool> WaitForClaimAsync(string projectRoot, DispatchClaimKey key, CancellationToken cancellationToken);
}
