using KyberWeave.Application.RunStart;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.RunStart;

/// <summary>
/// Whether a run has been started before an orchestrator hands a specialist work that writes files, for the callers
/// that have no container.
/// </summary>
/// <remarks>
/// The rules are <see cref="RunStartEvidenceRules"/> in the domain layer and the answer is
/// <see cref="RunStartEvidenceService"/> in the application layer behind <c>IRunStartEvidence</c>. This stays for the Arbiter
/// hook host and the tests that call it statically.
/// </remarks>
public static class RunStartEvidence
{
    /// <summary>The fix a refused dispatch names.</summary>
    public const string Remedy = RunStartEvidenceRules.Remedy;

    private static readonly RunStartEvidenceService Service = new(new GitFacts(), RunBaseState.Store);

    /// <inheritdoc cref="RunStartEvidenceRules.IsWriterProfile" />
    public static bool IsWriterProfile(string? capabilityProfile) => RunStartEvidenceRules.IsWriterProfile(capabilityProfile);

    /// <inheritdoc cref="KyberWeave.Contracts.Services.IRunStartEvidence.Started" />
    public static bool? Started(string workingDirectory, string homeDirectory) => Service.Started(workingDirectory, homeDirectory);
}
