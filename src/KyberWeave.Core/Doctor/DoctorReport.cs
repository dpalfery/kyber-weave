namespace KyberWeave.Core.Doctor;

/// <summary>The outcome of one doctor run.</summary>
/// <param name="Root">The resolved repository root, or the path the run was asked about when it refused.</param>
/// <param name="Mode">The mode the run used.</param>
/// <param name="ExitCode">
///     0 when every check passed; 1 when a check failed; 2 for a client-input refusal; 3 when a prerequisite is
///     missing and installing it needs the user's approval.
/// </param>
/// <param name="Checks">The checks that ran, in order.</param>
public sealed record DoctorReport(string Root, DoctorMode Mode, int ExitCode, IReadOnlyList<DoctorCheck> Checks);
