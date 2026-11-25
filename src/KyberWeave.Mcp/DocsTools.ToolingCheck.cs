using System.ComponentModel;
using ModelContextProtocol.Server;

namespace KyberWeave.Mcp;

/// <summary>
/// The tooling-check tool: the run-start check a caller with no shell runs in one call. An adapter over
/// <see cref="KyberWeave.Contracts.Services.IToolingCheckService"/>, which runs the same doctor as
/// <c>kyber-weave doctor --ensure</c>.
/// </summary>
public sealed partial class DocsTools
{
    /// <summary>The tooling-check tool's published name.</summary>
    public const string ToolingCheckToolName = "docs_run_tooling_check_and_repair_code_index";

    /// <summary>
    /// Runs the ensure-mode doctor against a checkout: syncs or creates its CodeGraph index, restores the tracked files
    /// the index run wrote, proves the index and the docs server with one call each, and repairs once on a failure.
    /// Reports one line per check, the doctor's exit code, and <c>STATUS: TOOL_FAILURE</c> when that code is not zero.
    /// </summary>
    [McpServerTool(Name = ToolingCheckToolName, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Use at the start of a run, or after a response carried STATUS: TOOL_FAILURE, to check and
        repair the tooling of one checkout or worktree without a shell: it does what kyber-weave
        doctor --ensure does — syncs the code index (or creates it), restores tracked files the
        index run touched, proves the code index and the docs server answer, and repairs once if a
        step fails. Returns one PASS or FAIL line per check, a fix: line under each failure, and a
        last line EXIT: <code> with the doctor's exit code (0 ready, 1 a check failed, 2 not a
        usable repository, 3 CodeGraph is not installed). Any non-zero code also carries the line
        STATUS: TOOL_FAILURE: stop and follow the fix: line. A repository that sets
        stop-on-tool-failure: false gets its failures as warnings and EXIT: 0. Writes only the
        checkout's .codegraph index and its post-checkout hook block; safe to call again.
        """)]
    public string RunToolingCheck(
        [Description("Absolute path of the checkout or worktree to check. Required.")] string projectPath)
    {
        if (Redirect(projectPath, tools => tools.Bound.ToolingCheck.RunToolingCheck()) is { } redirected)
            return redirected;
        if (string.IsNullOrWhiteSpace(projectPath))
            return ProvenanceLine() + "\nresult: refused\nprojectPath is required: pass the absolute path of the checkout or worktree to check.";
        return Bound.ToolingCheck.RunToolingCheck();
    }
}
