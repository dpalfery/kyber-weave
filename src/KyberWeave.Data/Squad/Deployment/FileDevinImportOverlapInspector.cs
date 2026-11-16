using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Inspects a workspace on disk through <see cref="DevinImportOverlap"/>.</summary>
public sealed class FileDevinImportOverlapInspector : IDevinImportOverlapInspector
{
    /// <inheritdoc />
    public DevinImportOverlapReport Inspect(
        string workingDirectory,
        string? devinUserRoot,
        Func<string, string?> readFileText) =>
        DevinImportOverlap.Inspect(workingDirectory, devinUserRoot, readFileText);
}
