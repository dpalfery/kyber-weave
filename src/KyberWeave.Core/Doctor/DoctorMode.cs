namespace KyberWeave.Core.Doctor;

/// <summary>Whether a doctor run may change the repository's CodeGraph index.</summary>
public enum DoctorMode
{
    /// <summary>Read-only. Detects the binary and index and proves the MCP server, and never runs <c>init</c> or <c>index</c>.</summary>
    Check,

    /// <summary>Provisions the CodeGraph index (<c>init</c> when absent, <c>index</c> otherwise), then proves the MCP server.</summary>
    Provision
}
