namespace KyberWeave.Core.Doctor;

/// <summary>The outcome of the CodeGraph side of a doctor run.</summary>
/// <param name="Checks">The <c>codegraph.*</c> checks, in order.</param>
/// <param name="BinaryMissing">True when the executable is not on PATH, which maps to exit 3.</param>
public sealed record CodeGraphOutcome(IReadOnlyList<DoctorCheck> Checks, bool BinaryMissing);
