using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Contracts.Services;

/// <summary>What the <c>utilities statusline</c> commands ask of the host: the artifact tree, <c>PATH</c>, and the deployed files.</summary>
public interface IStatusLineHostService
{
    /// <summary>Opens the status-line artifact tree of the current repository checkout.</summary>
    /// <exception cref="DirectoryNotFoundException">The checkout does not hold the artifact tree.</exception>
    IUtilitiesStatusLineArtifactSource ResolveArtifactSource();

    /// <summary>Whether <paramref name="executable"/> names an existing executable file in a <c>PATH</c> directory.</summary>
    /// <param name="executable">The program name to look for.</param>
    bool IsOnPath(string executable);

    /// <summary>Compares a receipt-owned file against its recorded SHA-256.</summary>
    /// <param name="physicalPath">The file on disk.</param>
    /// <param name="expectedSha256">The digest the receipt recorded.</param>
    UtilitiesStatusLineFileState EvaluateOwnedFile(string physicalPath, string expectedSha256);

    /// <summary>Whether a deployed file carries the user-execute bit. Always true where the platform has no such bit.</summary>
    /// <param name="physicalPath">The deployed file.</param>
    bool HasExecutableBit(string physicalPath);

    /// <summary>Marks the deployed command variants executable beneath <paramref name="stagingRoot"/>.</summary>
    /// <param name="stagingRoot">The harness staging root the files were written to.</param>
    /// <param name="deployed">The files the deploy wrote.</param>
    void ApplyExecutableBit(string stagingRoot, IReadOnlyList<StatusLineDeploymentFile> deployed);
}
