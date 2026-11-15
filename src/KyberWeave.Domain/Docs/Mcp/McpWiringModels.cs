using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>What kind of gap a wiring finding reports.</summary>
public enum McpWiringKind
{
    /// <summary>The harness is in play and does not configure the declared server.</summary>
    NotWired,

    /// <summary>The harness configures the server, but with a different launch command.</summary>
    CommandDiffers,

    /// <summary>The harness configures the server, but its launch binary is not resolvable.</summary>
    BinaryAbsent,

    /// <summary>The harness is not in play, or has no file in the tree to check.</summary>
    NotDetectable
}

/// <summary>Whether the repair can close a wiring gap.</summary>
public enum McpWiringFixability
{
    /// <summary>Kyber-Weave may write this harness's repository-scope configuration.</summary>
    Fixable,

    /// <summary>
    /// Kyber-Weave never writes this harness's configuration: the entry body is unverified, or
    /// there is no file to write. The gap is reported with the snippet to paste instead.
    /// </summary>
    NotFixable
}

/// <summary>One gap between a declared MCP server and a harness's repository-scope wiring.</summary>
/// <param name="Kind">What kind of gap this is.</param>
/// <param name="Severity">
/// The declared server's level: <c>required</c> warns, <c>recommended</c> informs. A
/// not-detectable line is always informational, whatever the level.
/// </param>
/// <param name="Harness">The harness slug.</param>
/// <param name="Server">
/// The server name as the declaration keys it. Empty on a not-detectable line: that line is about
/// the harness, and one is emitted per harness however many servers are declared.
/// </param>
/// <param name="Path">
/// The repository-relative configuration file the finding is about, or null when the harness has
/// no file in the tree.
/// </param>
/// <param name="Code">The stable <c>KW-*</c> rule id this finding carries.</param>
/// <param name="Message">One sentence naming the harness, file, server and gap.</param>
/// <param name="Hint">The remedy, as the command to run — not a description of one.</param>
/// <param name="Fixable">Whether the repair can close it.</param>
public sealed record McpWiringFinding(
    McpWiringKind Kind,
    Severity Severity,
    string Harness,
    string Server,
    string? Path,
    string Code,
    string Message,
    string Hint,
    McpWiringFixability Fixable);

/// <summary>Every finding one inspection produced, in report order.</summary>
/// <param name="Findings">
/// Ordered by harness in catalog order, then by server name (ordinal), so the same tree yields the
/// same report on every run and a diff of two reports means something.
/// </param>
public sealed record McpWiringReport(IReadOnlyList<McpWiringFinding> Findings);
