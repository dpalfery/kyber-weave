using System.ComponentModel;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>Settings for <c>docs packet</c>: the task a worker was dispatched for, and where its plan is.</summary>
public sealed class DocsPacketSettings : CommandSettings
{
    /// <summary>The task id, in any natural spelling: <c>T1</c>, <c>t1</c>, <c>task 1</c>, <c>1</c>.</summary>
    [CommandArgument(0, "<task>")]
    [Description("The task id from the dispatch, written any natural way: T1, t1, task 1, 1.")]
    public string Task { get; set; } = string.Empty;

    /// <summary>The plan, only when it cannot be found from the repository's own state.</summary>
    [CommandOption("--plan <PLAN>")]
    [Description("The plan, written any natural way. Leave it out: the one Ready or In progress plan is used (else the one in another git worktree that has the task), and several are listed.")]
    public string? Plan { get; set; }

    /// <summary>A file holding the earlier tasks' reported results.</summary>
    [CommandOption("--prior-results <FILE>")]
    [Description("A file with the results earlier tasks reported. Without it the packet's results section says none were supplied.")]
    public string? PriorResults { get; set; }

    /// <summary>The configuration file; defaults to the repository's own.</summary>
    [CommandOption("-c|--config <PATH>")]
    [Description("Path to kyber-weave.yml. Defaults to the repository's own.")]
    public string? Config { get; set; }

    /// <summary>The directory the command runs in; null means the process's current directory. A test seam, not an option.</summary>
    internal string? WorkingDirectory { get; set; }

    /// <summary>Lists the repository's worktree paths from a root; null means <c>git worktree list</c>. A test seam, not an option.</summary>
    internal Func<string, IReadOnlyList<string>>? ListWorktrees { get; set; }
}
