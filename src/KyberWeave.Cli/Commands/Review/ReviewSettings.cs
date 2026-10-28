using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>Settings for <c>review gates</c>.</summary>
public sealed class ReviewGatesSettings : AnalysisSettings
{
    [CommandOption("-o|--out <PATH>")]
    [Description("Write the gate report to this path as JSON. Defaults to stdout only.")]
    public string? Out { get; set; }

    [CommandOption("--stop-on-failure")]
    [Description("Skip the remaining gates once a blocking gate fails.")]
    public bool StopOnFailure { get; set; }

    [CommandOption("--agent <NAME>")]
    [Description("Run only this agent's task gates from review.task-gates, not the repository-wide gates.")]
    public string? Agent { get; set; }

    [CommandOption("--paths <PATH>")]
    [Description("Changed file to gate, relative to the repository root. Repeat for several. " +
                 "Defaults to the diff against --base plus untracked files.")]
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] Paths { get; set; } = [];

    [CommandOption("--base <REF>")]
    [Description("Git ref the working tree is diffed against when --paths is not given. Defaults to origin/HEAD " +
                 "for repository gates and to HEAD with --agent.")]
    public string? BaseRef { get; set; }

    [CommandOption("--task <SLUG>")]
    [Description("Task slug substituted for {task} in the agent's task gate commands. Requires --agent.")]
    public string? TaskSlug { get; set; }

    [CommandOption("--fix")]
    [Description("Run each task gate's fix command before its verify command. Requires --agent.")]
    public bool Fix { get; set; }

    /// <summary>
    /// Whether an option that only makes sense for one agent was given. <c>--paths</c> and <c>--base</c> also
    /// scope the repository-wide council gates, so they are not agent-only.
    /// </summary>
    public bool HasAgentOnlyOptions => TaskSlug is not null || Fix;
}

/// <summary>Settings for <c>review duplicates</c>.</summary>
public sealed class ReviewDuplicatesSettings : AnalysisSettings
{
    [CommandOption("-o|--out <PATH>")]
    [Description("Write the duplicates report to this path as JSON. Defaults to stdout only.")]
    public string? Out { get; set; }
}

/// <summary>Settings for <c>review verdict</c>.</summary>
public sealed class ReviewVerdictSettings : AnalysisSettings
{
    [CommandOption("--findings <PATH>")]
    [Description("Path to the council's findings JSON document.")]
    public string? Findings { get; set; }

    [CommandOption("--gates <PATH>")]
    [Description("Path to the gate report JSON produced by 'review gates'.")]
    public string? Gates { get; set; }
}
