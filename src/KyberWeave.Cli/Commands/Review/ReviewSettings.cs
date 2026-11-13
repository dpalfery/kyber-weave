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

    [CommandOption("--red")]
    [Description("Judge the agent's gate runs as a test-first task's failing run, after its tests are written and before it is implemented: they pass when tests ran and " +
                 "failed, or when the build failed only on missing-symbol errors (C# CS0103/CS0117/CS0234/CS0246/CS1061, " +
                 "TypeScript TS2304/TS2305/TS2307/TS2339) in the files the task changed; they fail when the build breaks " +
                 "for any other reason or every test passes. The report written is the proof of the failing run, and the run ends " +
                 "with the passing-run command to use next. Requires --agent.")]
    public bool Red { get; set; }

    [CommandOption("--all")]
    [Description("Run all declared repository gates on the host OS without include path filtering.")]
    public bool All { get; set; }

    [CommandOption("--gate <ID>")]
    [Description("Run only the repository gate with this id, in declaration order. Repeat to name several.")]
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] Gate { get; set; } = [];

    [CommandOption("--council")]
    [Description("Declare this run as the review council or CI, which alone may run gates marked full-suite. " +
                 "Without it, full-suite gates are refused when stdin is not a terminal.")]
    public bool Council { get; set; }

    [CommandOption("--deadline")]
    [Description("Bound the run by review.council-deadline-seconds (default 600). A gate the deadline or its own " +
                 "timeout stops is reported as not run (KW-REVIEW-039) and left out of the exit code. Without it, " +
                 "no run deadline applies and a timed-out blocking gate fails the run. Not with --gate or --agent.")]
    public bool Deadline { get; set; }

    [CommandOption("--full")]
    [Description("With --council, run every declared gate even in a verifier cycle. Without it, a council run in a " +
                 "cycle after the first runs only the gates whose inputs changed or that did not pass, and carries " +
                 "the others' earlier passing results into the report.")]
    public bool Full { get; set; }

    /// <summary>
    ///     Whether an option that only makes sense for one agent was given. <c>--paths</c> and <c>--base</c> also
    ///     scope the repository-wide council gates, so they are not agent-only.
    /// </summary>
    public bool HasAgentOnlyOptions => TaskSlug is not null || Fix || Red;
}

/// <summary>Settings for <c>review suppressions</c>.</summary>
public sealed class ReviewSuppressionsSettings : AnalysisSettings
{
    [CommandOption("--base <REF>")]
    [Description("Git ref the working tree is diffed against. Defaults to HEAD, so only this change's added " +
                 "lines are judged.")]
    public string? BaseRef { get; set; }
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

    [CommandOption("--seats <DIR>")]
    [Description("Folder of council seat output files, one <lens>.md per seat.")]
    public string? Seats { get; set; }

    [CommandOption("--drop <ID=REASON>")]
    [Description("Drop the finding with this id, giving the reason. Repeat for several.")]
    // reason: Spectre.Console.Cli binds repeated options to arrays.
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] Drop { get; set; } = [];

    [CommandOption("--not-seated <LENS=REASON>")]
    [Description("Record that this lens did not run, giving the reason. Repeat for several.")]
    // reason: Spectre.Console.Cli binds repeated options to arrays.
    [SuppressMessage(
        "Performance", "CA1819:Properties should not return arrays",
        Justification = "Spectre.Console.Cli binds repeated options to arrays.")]
    public string[] NotSeated { get; set; } = [];

    [CommandOption("--prior <DIR>")]
    [Description("Folder holding the previous cycle's findings.json (written by --out). A lens its coverage lists " +
                 "counts as covered, carried from that cycle; its findings are not re-read.")]
    public string? Prior { get; set; }

    [CommandOption("--base <REF>")]
    [Description("With --prior: git ref the change's base is resolved against when checking the prior cycle belongs to this change. Defaults to origin/HEAD.")]
    public string? BaseRef { get; set; }

    [CommandOption("--out <PATH>")]
    [Description("Write the findings document the verdict was computed from (after --drop and the cited-line " +
                 "check), with per-lens coverage, to this path as JSON. The verdict itself is printed, not written.")]
    public string? Out { get; set; }
}

/// <summary>Settings for <c>review cycle</c>.</summary>
public sealed class ReviewCycleSettings : AnalysisSettings
{
    [CommandOption("--task <ID>")]
    [Description("The task id whose review folder, .agent_scratchpad/<task-id>/review, holds the run's cycles. Any spelling: it is lower-cased and matched to the folder. Omitted: the only review folder, or the one named for the current branch.")]
    public string? Task { get; set; }

    [CommandOption("--base <REF>")]
    [Description("Git ref the change's base is resolved against when deciding which recorded cycles belong to this change. Defaults to origin/HEAD.")]
    public string? BaseRef { get; set; }
}
