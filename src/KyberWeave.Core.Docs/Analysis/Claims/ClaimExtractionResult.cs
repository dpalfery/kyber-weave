using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Analysis.Claims;

/// <summary>Claims and operational diagnostics produced from one document.</summary>
public sealed record ClaimExtractionResult(
    IReadOnlyList<Claim> Claims,
    DiagnosticReport Diagnostics);
