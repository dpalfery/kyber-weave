namespace KyberWeave.Core.Doctor;

/// <summary>Whether a doctor run may change the repository's CodeGraph index, and how far it goes to leave it usable.</summary>
public enum DoctorMode
{
    /// <summary>Read-only. Detects the binary and index and proves the MCP server, and never runs <c>init</c>, <c>sync</c> or <c>index</c>.</summary>
    Check,

    /// <summary>Provisions the CodeGraph index (<c>init</c> when absent, <c>sync</c> otherwise), then proves the MCP server.</summary>
    Provision,

    /// <summary>
    /// The one command a worker runs before it starts work: <c>sync</c> (or <c>init</c>), restore any tracked edit the
    /// initialisation wrote, one <c>codegraph explore</c>, one docs tool call, and a single repair with <c>codegraph index</c>
    /// followed by a single retry when either step fails.
    /// </summary>
    Ensure
}
