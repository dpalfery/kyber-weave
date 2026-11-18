using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds the Squad identities Devin would load twice in one workspace.</summary>
public interface IDevinImportOverlapInspector
{
    /// <summary>
    /// Reports every tree Devin would load that repeats an identity <c>.devin/</c> defines,
    /// leaving out a tree whose import the effective configuration turns off.
    /// </summary>
    /// <param name="workingDirectory">The workspace a Devin session would open.</param>
    /// <param name="devinUserRoot">The Devin user configuration directory, or null when it cannot be resolved.</param>
    /// <returns>The overlapping trees.</returns>
    DevinImportOverlapReport Inspect(
        string workingDirectory,
        string? devinUserRoot);
}
