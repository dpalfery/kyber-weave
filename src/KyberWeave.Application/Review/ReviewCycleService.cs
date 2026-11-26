using KyberWeave.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Application.Review;

/// <summary>
/// Answers which review cycle a run is in, from the cycle folders the council already wrote.
/// </summary>
/// <remarks>
/// Whether a cycle is a verifier cycle used to depend on how the conductor worded the dispatch. It is now a fact of
/// the folders: a run with a finished cycle is in a verifier cycle. The service reads and writes nothing else.
/// </remarks>
/// <param name="trees">Finds the git work tree the review belongs to.</param>
/// <param name="cycles">Reads the recorded review cycle folders.</param>
/// <param name="selector">Finds the review folder and the prior cycle's findings.</param>
/// <param name="configs">Loads the host's configuration.</param>
/// <param name="paths">Resolves the path the command was run against.</param>
/// <param name="environment">Reports the operating-system family a gate's <c>os</c> filter is matched against.</param>
public sealed class ReviewCycleService(
    IReviewWorkTreeService trees,
    IReviewCycleStore cycles,
    IReviewCycleSelector selector,
    IConfigLoadService configs,
    IPathResolver paths,
    IProcessEnvironment environment) : IReviewCycleService
{
    private const string UnreadableInput = "KW-REVIEW-023";

    /// <inheritdoc />
    public ReviewCommandResult Run(
        ReviewCycleRequest request,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);

        string root = paths.GetFullPath(request.Path, environment.CurrentDirectory);
        DiagnosticReport report = new();
        if (!configs.TryLoad(root, request.Config, report, out KyberWeaveConfig config, out _))
            return ReviewCommandResultFactory.Reported(1, report);

        // The review folder belongs to the git work tree that contains the path the command was run against: a linked
        // worktree has its own .agent_scratchpad, and the canonical checkout's is never read on its behalf.
        string tree = trees.Containing(root);
        string scratchpad = ReviewWorkTree.Scratchpad(tree);
        ReviewTaskChoice choice = selector.ChooseTask(tree, scratchpad, request.Task);
        if (!choice.Chosen)
            return ReviewCommandResultFactory.Refused(choice.Error!);

        string task = choice.Task;
        if (choice.Note is { } chosen && !request.Json)
            output.WriteLine(chosen);

        string? liveRun = trees.LiveRunNote(tree, trees.HomeDirectory());
        if (liveRun is not null && !request.Json)
            output.WriteLine(liveRun);

        string reviewFolder = Path.Combine(scratchpad, task, ScratchLayout.ReviewFolderName);
        PriorCycles prior = trees.Prior(tree, request.BaseRef, cycles.ReadFinished(reviewFolder));
        ReviewCyclePlan plan = ReviewCyclePlanning.Plan(
            prior.Valid,
            config.Review.Gates,
            ReviewGateReporting.CurrentOs(environment),
            commit => selector.ChangedSince(tree, commit));

        IReadOnlyList<ReviewFinding> recheck = [];
        if (plan.Prior is { } priorCycle)
        {
            (bool ok, IReadOnlyList<ReviewFinding> found) = selector.ReadRecheck(
                priorCycle, config.Review.Policy, report, UnreadableInput, Severity.Error, "review cycle");
            if (!ok)
                return ReviewCommandResultFactory.Reported(1, report);

            recheck = found;
        }

        string folder = Path.Combine(reviewFolder, ScratchLayout.CycleFolder(plan.Number));
        string command = ReviewCycleView.GatesCommand(root, ScratchLayout.CycleGates(task, plan.Number), request.BaseRef);
        if (request.Json)
            output.WriteLine(ToJson(tree, plan, folder, recheck, prior.Ignored, liveRun, command));
        else
            WriteLines(output, tree, plan, folder, recheck, prior.Ignored, command);

        return new ReviewCommandResult(0);
    }

    private static void WriteLines(
        TextWriter output,
        string tree,
        ReviewCyclePlan plan,
        string folder,
        IReadOnlyList<ReviewFinding> recheck,
        IReadOnlyList<IgnoredCycle> ignored,
        string command)
    {
        output.WriteLine($"cycle: {plan.Number}");
        output.WriteLine($"mode: {ReviewCycleView.Mode(plan)}");
        output.WriteLine($"folder: {folder}");
        foreach (string line in ReviewCycleView.IgnoredLines(tree, ignored))
            output.WriteLine(line);

        if (plan.Prior is null)
        {
            output.WriteLine("gates: run every declared gate");
        }
        else
        {
            foreach (string line in ReviewCycleView.VerifierLines(plan, recheck))
                output.WriteLine(line);
        }

        output.WriteLine($"run: {command}");
    }

    private static string ToJson(
        string tree,
        ReviewCyclePlan plan,
        string folder,
        IReadOnlyList<ReviewFinding> recheck,
        IReadOnlyList<IgnoredCycle> ignored,
        string? liveRun,
        string command)
    {
        JsonObject answer = new()
        {
            ["cycle"] = plan.Number,
            ["mode"] = ReviewCycleView.Mode(plan),
            ["folder"] = folder,
            ["command"] = command,
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
}
