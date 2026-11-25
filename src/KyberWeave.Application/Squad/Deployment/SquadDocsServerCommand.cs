using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Decides the command an Antigravity <c>kw-docs</c> entry launches on a global render, so the install, update and
/// doctor renders produce the same bytes for the same machine.
/// </summary>
/// <remarks>
/// The lookup is the one seam that differs by machine: a global render uses the absolute path PATH names, a project
/// render uses the bare name. Install and update resolve it here and hand the result to the render and to the config
/// entry; doctor resolves it here too, so its expected agent files match what install wrote.
/// </remarks>
public static class SquadDocsServerCommand
{
    /// <summary>The executable the <c>kw-docs</c> entry launches.</summary>
    public const string Executable = "kyber-weave-mcp";

    /// <summary>
    /// The argument the <c>kw-docs</c> entry passes so one server answers for whichever repository the client works in.
    /// Antigravity's user MCP config entry and its rendered agent entry both read it, so the two launch the same server.
    /// </summary>
    public const string AnyRepoArgument = "--any-repo";

    /// <summary>
    /// The absolute path PATH names for <see cref="Executable"/> on a global render, or null for a project render, when
    /// the lookup finds nothing, or when no lookup is wired. The path is not resolved through symbolic links, so a
    /// self-update that swaps the link's target keeps the entry working.
    /// </summary>
    /// <remarks>
    /// The answer is written into the operator's Antigravity config and agent files, which launch it later from any
    /// directory, so the lookup is not taken on trust: an answer that is not fully qualified, or that names no existing
    /// file, is treated as not found and the entry falls back to the bare name.
    /// </remarks>
    /// <param name="renderScope">The scope the render writes to; the lifecycle maps an instance render to global.</param>
    /// <param name="findExecutable">Finds an executable on PATH, or null when none is wired.</param>
    /// <param name="fileExists">Tells whether a file exists, following a symbolic link to the file it names.</param>
    /// <returns>The path, or null.</returns>
    public static string? Resolve(
        SquadDeploymentScope renderScope,
        Func<string, string?>? findExecutable,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        if (renderScope != SquadDeploymentScope.Global || findExecutable is null)
            return null;

        string? found = findExecutable(Executable);
        if (string.IsNullOrWhiteSpace(found) || !Path.IsPathFullyQualified(found) || !fileExists(found))
            return null;

        return found;
    }

    /// <summary>
    /// One warning when Antigravity is targeted globally, a lookup is wired, and it found no <see cref="Executable"/>:
    /// the Antigravity entry and agent files then fall back to the bare command name.
    /// </summary>
    /// <param name="renderScope">The scope the render writes to.</param>
    /// <param name="targets">The harnesses the deployment renders.</param>
    /// <param name="findExecutable">The lookup that was wired, or null.</param>
    /// <param name="resolved">What <see cref="Resolve"/> returned.</param>
    /// <returns>Nothing, or the one line to report.</returns>
    public static IReadOnlyList<string> MissingWarnings(
        SquadDeploymentScope renderScope,
        IReadOnlyCollection<SquadTarget> targets,
        Func<string, string?>? findExecutable,
        string? resolved)
    {
        if (renderScope != SquadDeploymentScope.Global
            || findExecutable is null
            || resolved is not null
            || !targets.Contains(SquadTarget.Antigravity))
            return [];

        return [$"{Executable} was not found on PATH, so the Antigravity kw-docs entry and agent files use the bare command name."];
    }
}
