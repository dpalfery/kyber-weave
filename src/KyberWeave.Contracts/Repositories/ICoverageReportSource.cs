using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the Cobertura coverage reports a test gate wrote under a repository.</summary>
public interface ICoverageReportSource
{
    /// <summary>The newest report under <c>artifacts/coverage</c> or <c>TestResults</c> written at or after <paramref name="notBeforeUtc"/>, or null when the gate produced none.</summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="notBeforeUtc">The time the gate run started.</param>
    CoverageResult? ReadNewest(string workingDirectory, DateTime notBeforeUtc);

    /// <summary>Every report under <c>artifacts</c> or <c>TestResults</c> written at or after <paramref name="notBeforeUtc"/>, in path order.</summary>
    /// <param name="workingDirectory">The repository root.</param>
    /// <param name="notBeforeUtc">The time the gate run started.</param>
    IReadOnlyList<CoverageReport> ReadAll(string workingDirectory, DateTime notBeforeUtc);

    /// <summary>Reads one report file; null when it is not a Cobertura report or cannot be read.</summary>
    /// <param name="path">The report file.</param>
    CoverageResult? ReadFile(string path);
}
