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

    private const string InvalidInput = "KW-REVIEW-028";
    private const string UnreadableInput = "KW-REVIEW-023";
    private const int ClientInputExitCode = 2;

    /// <inheritdoc />
    public override int Execute(CommandContext context, ReviewCycleSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Task))
        {
            WriteClientInputError($"{InvalidInput}: --task is required. Name the task id whose review folder to read.");
            return ClientInputExitCode;
        }

        if (TaskGateSelection.ValidateTask(settings.Task) is { } taskProblem)
        {
            WriteClientInputError($"{InvalidInput}: {taskProblem}");
            return ClientInputExitCode;
        }

        string root = Path.GetFullPath(settings.Path);
        DiagnosticReport report = new();
        if (!CommandHelpers.TryLoadConfig(root, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "review cycle", "Review");
            return 1;
        }

        string reviewFolder = Path.Combine(root, ".agent_scratchpad", settings.Task, "review");
        IReadOnlyList<ReviewCycleRecord> finished = ReviewCycles.ReadFinished(reviewFolder);
        ReviewCyclePlan plan = ReviewCycles.Plan(
            finished,
            config.Review.Gates,
            OperatingSystem.IsWindows() ? "windows" : "unix",
            commit => TryChangedSince(root, commit));

        List<ReviewFinding> recheck = [];
        if (plan.Prior is { } prior && !TryReadPriorFindings(prior, report, recheck))
        {
            CommandHelpers.Finish(report, settings, "review cycle", "Review");
            return 1;
        }

        string folder = Relative(root, Path.Combine(reviewFolder, $"cycle-{plan.Number}"));
        if (settings.ParsedFormat == KyberWeave.Cli.Rendering.OutputFormat.Json)
            Console.WriteLine(ToJson(root, plan, folder, recheck));
        else
            WriteLines(root, plan, folder, recheck);

        return 0;
    }

    private static IReadOnlyList<string>? TryChangedSince(string root, string commit)
    {
        try
        {
            return ChangedFiles.ResolveSince(root, commit);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or TimeoutException)
        {
            return null;
        }
    }

    private static bool TryReadPriorFindings(ReviewCycleRecord prior, DiagnosticReport report, List<ReviewFinding> recheck)
    {
        try
        {
            FindingsReport findings = ReviewJson.ReadFindings(File.ReadAllText(prior.FindingsPath));
            recheck.AddRange(findings.Findings.Where(finding => finding.Severity >= ReviewSeverity.Major));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            report.Add(new Diagnostic(
                UnreadableInput,
                Severity.Error,
                $"Could not read '{prior.FindingsPath}': {ex.Message}",
                "review cycle",
                prior.FindingsPath,
                Hint: "Compute the prior cycle's verdict again with 'review verdict --out', then ask again."));
            return false;
        }
    }

    private static void WriteLines(string root, ReviewCyclePlan plan, string folder, List<ReviewFinding> recheck)
    {
        Say($"cycle: {plan.Number}");
        Say($"mode: {Mode(plan)}");
        Say($"folder: {folder}");
        if (plan.Prior is not { } prior)
        {
            Say("gates: run every declared gate");
            return;
        }

        Say($"prior: {Relative(root, prior.Folder)}");
        Say($"prior-head: {prior.Head ?? "unknown"}");
        Say($"findings: {Relative(root, prior.FindingsPath)}");
        foreach (ReviewFinding finding in recheck)
            Say($"recheck: {finding.Id} ({SeverityName(finding.Severity)}) {finding.File}:{finding.Line}");

        if (recheck.Count == 0)
            Say("recheck: none (the prior cycle left no critical or major finding)");

        foreach (GateCycleDecision gate in plan.Gates.Where(gate => gate.Action != GateCycleAction.NotApplicable))
            Say($"{(gate.Action == GateCycleAction.Rerun ? "rerun" : "carry")}: {gate.GateId} ({gate.Reason})");
    }

    private static string Mode(ReviewCyclePlan plan) => plan.IsVerifier ? "verifier" : "full";

    private static string SeverityName(ReviewSeverity severity) =>
        severity == ReviewSeverity.Critical ? "critical" : "major";

    private static void Say(string line) => Console.WriteLine(line);

    private static string ToJson(string root, ReviewCyclePlan plan, string folder, List<ReviewFinding> recheck)
    {
        JsonObject answer = new()
        {
            ["cycle"] = plan.Number,
            ["mode"] = Mode(plan),
            ["folder"] = folder
        };

        if (plan.Prior is { } prior)
        {
            answer["prior"] = Relative(root, prior.Folder);
            answer["priorHead"] = prior.Head;
            answer["findings"] = Relative(root, prior.FindingsPath);
            answer["recheck"] = new JsonArray([.. recheck.Select(finding => (JsonNode?)finding.Id)]);
            answer["rerunGates"] = Ids(plan, GateCycleAction.Rerun);
            answer["carryGates"] = Ids(plan, GateCycleAction.Carry);
        }

        return answer.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray Ids(ReviewCyclePlan plan, GateCycleAction action) =>
        new([.. plan.Gates.Where(gate => gate.Action == action).Select(gate => (JsonNode?)gate.GateId)]);

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void WriteClientInputError(string message)
    {
        Console.WriteLine($"kyber-weave review cycle: error: {message}");
    }
}
