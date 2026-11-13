using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Review;

/// <summary>
///     Answers which review cycle a run is in, from the cycle folders the council already wrote: the cycle number,
///     whether it is a full review or a verifier cycle, the prior findings to re-check, and the gates to run again.
/// </summary>
/// <remarks>
///     Whether a cycle is a verifier cycle used to depend on how the conductor worded the dispatch. It is now a fact
///     of the folders: a run with a finished cycle is in a verifier cycle. The command reads and writes nothing else.
/// </remarks>
public sealed class ReviewCycleCommand : Command<ReviewCycleSettings>
{
    /// <summary>The command description shared by the real registration and the help tests.</summary>
    public const string Description = "Report which review cycle a task is in, the prior findings to re-check and the gates to re-run.";

    private const string UnreadableInput = "KW-REVIEW-023";
    private const int ClientInputExitCode = 2;

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewCycleSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string root = Path.GetFullPath(settings.Path);
        DiagnosticReport report = new();
        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review cycle", "Review");
            return 1;
        }

        // The review folder belongs to the git work tree that contains the path the command was run against: a linked
        // worktree has its own .agent_scratchpad, and the canonical checkout's is never read on its behalf.
        string tree = ReviewWorkTree.Of(root);
        string scratchpad = ReviewWorkTree.Scratchpad(tree);
        if (!ReviewCycleView.TryChooseTask(tree, scratchpad, settings.Task, out string task, out string? chosen, out string? problem))
        {
            WriteClientInputError(problem!);
            return ClientInputExitCode;
        }

        if (chosen is not null)
            Console.WriteLine(chosen);

        string? liveRun = ReviewWorkTree.LiveRunNote(tree, ReviewWorkTree.HomeDirectory());
        bool json = settings.ParsedFormat == KyberWeave.Cli.Rendering.OutputFormat.Json;
        if (liveRun is not null && !json)
            Console.WriteLine(liveRun);

        string reviewFolder = Path.Combine(scratchpad, task, "review");
        PriorCycles prior = ReviewWorkTree.Prior(tree, settings.BaseRef, ReviewCycles.ReadFinished(reviewFolder));
        ReviewCyclePlan plan = ReviewCycles.Plan(
            prior.Valid,
            config.Review.Gates,
            OperatingSystem.IsWindows() ? "windows" : "unix",
            commit => ReviewCycleView.TryChangedSince(tree, commit));

        List<ReviewFinding> recheck = [];
        if (plan.Prior is { } priorCycle && !ReviewCycleView.TryReadRecheck(priorCycle, report, recheck, UnreadableInput, Severity.Error, "review cycle"))
        {
            CommandHelpers.Finish(report, settings, "review cycle", "Review");
            return 1;
        }

        string folder = Path.Combine(reviewFolder, $"cycle-{plan.Number}");
        if (json)
            Console.WriteLine(ToJson(tree, plan, folder, recheck, prior.Ignored, liveRun));
        else
            WriteLines(tree, plan, folder, recheck, prior.Ignored);

        return 0;
    }

    private static void WriteLines(string tree, ReviewCyclePlan plan, string folder, List<ReviewFinding> recheck, IReadOnlyList<IgnoredCycle> ignored)
    {
        Say($"cycle: {plan.Number}");
        Say($"mode: {ReviewCycleView.Mode(plan)}");
        Say($"folder: {folder}");
        foreach (string line in ReviewCycleView.IgnoredLines(tree, ignored))
            Say(line);

        if (plan.Prior is null)
        {
            Say("gates: run every declared gate");
            return;
        }

        foreach (string line in ReviewCycleView.VerifierLines(plan, recheck))
            Say(line);
    }

    private static void Say(string line) => Console.WriteLine(line);

    private static string ToJson(string tree, ReviewCyclePlan plan, string folder, List<ReviewFinding> recheck, IReadOnlyList<IgnoredCycle> ignored, string? liveRun)
    {
        JsonObject answer = new()
        {
            ["cycle"] = plan.Number,
            ["mode"] = ReviewCycleView.Mode(plan),
            ["folder"] = folder,
            ["ignored"] = new JsonArray([.. ignored.Select(cycle => (JsonNode?)new JsonObject
            {
                ["folder"] = ReviewCycleView.Relative(tree, cycle.Record.Folder),
                ["reason"] = cycle.Reason
            })])
        };

        if (liveRun is not null)
            answer["worktree"] = liveRun;

        if (plan.Prior is { } prior)
        {
            answer["prior"] = prior.Folder;
            answer["priorHead"] = prior.Head;
            answer["findings"] = prior.FindingsPath;
            answer["recheck"] = new JsonArray([.. recheck.Select(finding => (JsonNode?)finding.Id)]);
            answer["rerunGates"] = Ids(plan, GateCycleAction.Rerun);
            answer["carryGates"] = Ids(plan, GateCycleAction.Carry);
        }

        return answer.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray Ids(ReviewCyclePlan plan, GateCycleAction action) =>
        new([.. plan.Gates.Where(gate => gate.Action == action).Select(gate => (JsonNode?)gate.GateId)]);

    private static void WriteClientInputError(string message)
    {
        Console.WriteLine($"kyber-weave review cycle: error: {message}");
    }
}
