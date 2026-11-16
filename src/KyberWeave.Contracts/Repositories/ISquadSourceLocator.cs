namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds the canonical Kyber-Squad product source inside a repository checkout.</summary>
public interface ISquadSourceLocator
{
    /// <summary>Checks the working directory itself, without searching parents, for the solution marker and the squad manifest.</summary>
    /// <param name="workingDirectory">The directory to inspect.</param>
    /// <returns>The full path to <c>products/kyber-squad</c> when both markers exist; otherwise null.</returns>
    string? Resolve(string workingDirectory);
}
