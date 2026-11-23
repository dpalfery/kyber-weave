using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Run;

/// <summary>
/// Settings for <c>kyber-weave run integrate</c>: the task whose worktree is integrated, the run worktree it is
/// integrated into, and where the task worktree is.
/// </summary>
public sealed class RunIntegrateSettings : CommandSettings
{
    /// <summary>The task id, which also names the scratchpad folder the integration keeps.</summary>
    [CommandOption("--task <ID>")]
    [Description(
        "The task id whose task worktree is integrated. Required: it names the .agent_scratchpad folder that " +
        "is copied into the run worktree.")]
    public string? Task { get; set; }

    /// <summary>The run's worktree, when it is not the live run record.</summary>
    [CommandOption("--run-worktree <PATH>")]
    [Description(
        "The run's worktree. Default: the live run record run start wrote for this repository, from the operator's home.")]
    public string? RunWorktree { get; set; }

    /// <summary>The task's worktree, when it is not the current directory.</summary>
    [CommandOption("--path <PATH>")]
    [Description("The task worktree to integrate. Default: the current directory.")]
    public string? Path { get; set; }
}
