namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The name of the cross-process lease that serialises changes to one deployment root.</summary>
public static class SquadLeaseNames
{
    /// <summary>Gets the lease name for a deployment root.</summary>
    /// <param name="rootKey">The lease identity the state store resolves for the root.</param>
    /// <returns>The name every writer of that root's state takes before it changes anything.</returns>
    public static string ForRoot(string rootKey) => $"kyber-weave-squad-{rootKey}";
}
