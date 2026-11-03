namespace KyberWeave.Core.Doctor;

/// <summary>One named check and its outcome.</summary>
/// <param name="Id">The stable check id, such as <c>codegraph.index</c> or <c>mcp.root</c>. Agents branch on it.</param>
/// <param name="Passed">Whether the check passed.</param>
/// <param name="Reason">A short, non-sensitive reason. Never the child's environment.</param>
/// <param name="Fix">The exact command that resolves a failure. Null when the check passed.</param>
public sealed record DoctorCheck(string Id, bool Passed, string? Reason, string? Fix);
