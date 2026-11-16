namespace KyberWeave.Domain.Doctor;

/// <summary>The result of ensuring one tree's CodeGraph index.</summary>
/// <param name="Status">The index state.</param>
/// <param name="Created">True when this call created the index with <c>codegraph init</c> and it is ready now.</param>
/// <param name="StopsRun">True when the caller must stop: the index is unusable and the repository stops on tool failure.</param>
/// <param name="Checks">The <c>codegraph.*</c> checks, in order, for the caller to report.</param>
public sealed record WorktreeIndexOutcome(
    WorktreeIndexStatus Status,
    bool Created,
    bool StopsRun,
    IReadOnlyList<DoctorCheck> Checks);
