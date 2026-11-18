using System.Globalization;
using System.Text.Json;
using KyberWeave.Cli.Commands.Doctor;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Cli.Commands.Squad.Brief;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Core.RunStart;
using KyberWeave.Core.Squad.Briefs;
using Spectre.Console;
using Spectre.Console.Cli;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Cli.Commands.Run;

/// <summary>
///     Starts a run: fetches, resolves the branch the work belongs on, and creates it from the right remote
///     base before anyone investigates or plans.
/// </summary>
/// <remarks>
///     <para>
///         The order is the point. A run that plans against stale history is the failure this step exists to
///         prevent, so the fetch comes first and a fetch failure stops the run unless the host turned
///         <c>stop-on-tool-failure</c> off. Nothing is written until the base, the branch name and the place
///         the branch goes are all decided.
///     </para>
///     <para>
///         Exit codes are a contract a caller branches on: <c>0</c> ready, <c>1</c> a tool failed under
///         <c>stop-on-tool-failure</c> or the repository has not answered onboarding, <c>2</c> refused input,
///         <c>3</c> the CodeGraph binary is missing. Onboarding is checked before the tool checks, so <c>1</c>
///         reported for a missing tool never masks it.
///     </para>
/// </remarks>
public sealed class RunStartCommand : Command<RunStartSettings>
{
    /// <summary>The outcome a caller acts on: the branch exists and the run may continue.</summary>
    private const int Ready = 0;

    /// <summary>A tool this step depends on failed, and the host asked a failure to stop the run.</summary>
    private const int ToolFailure = 1;

    /// <summary>The caller supplied input this command cannot act on.</summary>
    private const int RefusedInput = 2;

    /// <summary>The CodeGraph binary is not installed, which has its own remedy.</summary>
    private const int CodeGraphMissing = 3;

    private const string CommandName = "kyber-weave run start";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IRunStartRunnerFactory _runners;
    private readonly IRunStartService _starts;
    private readonly ISquadBriefBuilder _briefs;
    private readonly IConfigLoadService _configs;
    private readonly IProcessEnvironment _environment;
    private readonly IAnsiConsole _console;
    private readonly IConsole _standard;
    private readonly IProcessExecutor? _executor;

    /// <summary>Creates the command over the services it reads and writes through.</summary>
    /// <param name="runners">Opens the git runner for the repository the run starts in.</param>
    /// <param name="starts">Fetches, resolves the base and creates the branch.</param>
    /// <param name="briefs">Builds the conductor's brief.</param>
    /// <param name="configs">Loads the repository's configuration.</param>
    /// <param name="environment">The current directory.</param>
    /// <param name="console">Where the report, the messages and the refusals are written.</param>
    /// <param name="standard">Standard output for the JSON document and the brief, and standard error for the messages a JSON caller must not mix in.</param>
    /// <param name="executor">The executor the CodeGraph index run goes through; the bounded production executor when none is registered.</param>
    public RunStartCommand(
        IRunStartRunnerFactory runners,
        IRunStartService starts,
        ISquadBriefBuilder briefs,
        IConfigLoadService configs,
        IProcessEnvironment environment,
        IAnsiConsole console,
        IConsole standard,
        IProcessExecutor? executor = null)
    {
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(starts);
        ArgumentNullException.ThrowIfNull(briefs);
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(standard);

        _runners = runners;
        _starts = starts;
        _briefs = briefs;
        _configs = configs;
        _environment = environment;
        _console = console;
        _standard = standard;
        _executor = executor;
    }

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        RunStartSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!RunStartPlanner.TryNameBranch(
                settings.Harness,
                settings.Type,
                settings.Slug,
                out string branch,
                out string refusal))
        {
            return Refuse(refusal);
        }

        if (!settings.Brief && (settings.Path is not null || settings.Autonomy is not null))
        {
            return Refuse("--path and --autonomy only apply with --brief.");
        }

        if (settings.Brief && !_briefs.AcceptsFacts(settings.Path, settings.Autonomy, out string briefRefusal))
        {
            return Refuse(briefRefusal);
        }

        string currentDirectory = _environment.CurrentDirectory;
        string? repositoryRoot = _runners.Open(currentDirectory).DetectRepositoryRoot();
        if (repositoryRoot is null)
        {
            return Fail(
                $"'{currentDirectory}' is not inside a git repository, so there is no checkout " +
                "to fetch into or to create the branch in.");
        }

        DiagnosticReport report = new();
        if (!_configs.TryLoad(repositoryRoot, null, report, out KyberWeaveConfig config, out string? configPath))
        {
            WriteDiagnostics(report);
            return ToolFailure;
        }

        RunStartOutcome outcome = _starts.Start(
            new RunStartRequest(repositoryRoot, branch, settings.Resume, config, configPath, _executor));
        WriteMessages(outcome.Messages, settings.Json);

        if (outcome.Facts is not { } facts)
        {
            return outcome.Status switch
            {
                RunStartStatus.RefusedInput => RefusedInput,
                RunStartStatus.CodeGraphMissing => CodeGraphMissing,
                _ => ToolFailure,
            };
        }

        string indexTree = facts.IndexTree;
        string? worktreePath = facts.WorktreePath;
        BriefResult? brief = settings.Brief
            ? BuildBrief(settings, config, indexTree, repositoryRoot, worktreePath is not null, runHarness: settings.Harness)
            : null;

        if (settings.Json)
        {
            object json = new
            {
                @base = facts.BaseBranch,
                baseRef = facts.StartRef,
                branch = facts.Branch,
                worktree = worktreePath,
                location = facts.Location == RunStartLocation.InPlace ? "in-place" : "worktree",
                revision = facts.BaseRevision,
                resumed = facts.ResumeTarget is not null,
                index = facts.IndexLabel,
                completed = facts.Completed.Select(task => new { id = task.Id, report = task.Report }),
                completedWarning = facts.CompletedWarning,
                versions = new
                {
                    failing = settings.FailingVersion,
                    head = facts.HeadVersion,
                    @base = facts.BaseVersion
                },
                serverAlignment = new
                {
                    remote = RunStartPlanner.Remote,
                    host = RunStartPlanner.HostOf(facts.OriginAddress),
                    remoteUrl = facts.OriginAddress,
                    head = facts.AlignmentRevision,
                    ahead = facts.Ahead,
                    behind = facts.Behind,
                    aligned = facts.Ahead is not null && facts.Behind is not null
                            ? facts.Ahead == 0 && facts.Behind == 0
                            : (bool?)null
                }
            };

            JsonNode document = JsonSerializer.SerializeToNode(json, JsonOptions)!;
            if (brief is not null)
            {
                document["brief"] = JsonNode.Parse(BriefFormatter.ToJson(brief));
            }

            _standard.Out.WriteLine(document.ToJsonString(JsonOptions));
            return Ready;
        }

        WriteReport(
            facts.BaseBranch,
            facts.StartRef,
            facts.Branch,
            facts.Location,
            worktreePath,
            facts.BaseRevision,
            facts.ResumeTarget,
            settings.FailingVersion,
            facts.HeadVersion,
            facts.BaseVersion,
            facts.Ahead,
            facts.Behind,
            facts.HeadRevision,
            facts.Completed,
            facts.CompletedWarning,
            facts.IndexLabel);

        if (brief is not null)
        {
            _standard.Out.Write("\n" + BriefFormatter.ToText(brief));
        }

        return Ready;
    }

    /// <summary>Prints what the step has to tell the operator, in the order it came.</summary>
    /// <param name="messages">The lines the service collected.</param>
    /// <param name="json">Whether standard output must stay one JSON document.</param>
    private void WriteMessages(IReadOnlyList<RunStartMessage> messages, bool json)
    {
        foreach (RunStartMessage message in messages)
        {
            if (json && message.StandardErrorWhenJson)
            {
                _standard.Error.WriteLine(message.Kind switch
                {
                    RunStartMessageKind.Error => $"{CommandName}: error: {message.Text}",
                    RunStartMessageKind.Remedy => $"→ {message.Text}",
                    RunStartMessageKind.Warning => $"{CommandName}: warn: {message.Text}",
                    _ => message.Text,
                });
                continue;
            }

            _console.MarkupLine(message.Kind switch
            {
                RunStartMessageKind.Error => $"[red]{Markup.Escape(CommandName)}: error:[/] {Markup.Escape(message.Text)}",
                RunStartMessageKind.Remedy => $"[grey]→ {Markup.Escape(message.Text)}[/]",
                RunStartMessageKind.Warning => $"[yellow]warn[/] {Markup.Escape(message.Text)}",
                _ => $"[grey]  {Markup.Escape(message.Text)}[/]",
            });
        }
    }

    /// <summary>The two-argument overload the rest of this branch's commands carry.</summary>
    /// <param name="context">The command context Spectre supplies.</param>
    /// <param name="settings">The parsed settings.</param>
    public int Execute(CommandContext context, RunStartSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>
    ///     Builds the conductor's brief for the run just set up. The branch already exists, so a brief that
    ///     cannot be built is a warning and the run is still ready: the conductor falls back to reading the
    ///     reference files whole.
    /// </summary>
    private BriefResult? BuildBrief(
        RunStartSettings settings,
        KyberWeaveConfig config,
        string runTree,
        string repositoryRoot,
        bool inWorktree,
        string? runHarness)
    {
        string[] roots = inWorktree ? [runTree, repositoryRoot] : [repositoryRoot];
        SquadBriefRequest request = new(
            "conductor",
            settings.Path,
            settings.Autonomy,
            null,
            null,
            runHarness,
            null,
            runTree,
            roots,
            config);

        if (_briefs.TryBuild(request, out BriefResult? result, out string error))
        {
            return result;
        }

        string warning = $"the conductor's brief could not be built ({error}); read its reference files whole.";
        if (settings.Json)
        {
            _standard.Error.WriteLine($"{CommandName}: warn: {warning}");
        }
        else
        {
            _console.MarkupLine($"[yellow]warn[/] {Markup.Escape(warning)}");
        }

        return null;
    }

    private void WriteReport(
        string baseBranch,
        string startRef,
        string branch,
        RunStartLocation location,
        string? worktreePath,
        string? revision,
        string? resumed,
        string? failingVersion,
        string headVersion,
        string baseVersion,
        int? ahead,
        int? behind,
        string? headRevision,
        IReadOnlyList<RunStartCompletedTask> completed,
        string? completedWarning,
        string indexLabel)
    {
        string where = worktreePath is null ? "in place" : worktreePath;

        CommandHelpers.WriteUnfolded(_console, () =>
        {
            _console.MarkupLine(
                $"[bold]{Markup.Escape(CommandName)}[/] " +
                $"{(resumed is null ? "ready" : "resumed")}");
            _console.MarkupLine(
                $"  base      {Markup.Escape(baseBranch)} ({Markup.Escape(startRef)})");
            _console.MarkupLine($"  branch    {Markup.Escape(branch)}");
            _console.MarkupLine($"  where     {Markup.Escape(where)}");
            _console.MarkupLine($"  index     {Markup.Escape(indexLabel)}");
            _console.MarkupLine($"  revision  {Markup.Escape(revision ?? "unknown")}");
            _console.MarkupLine("  versions");
            _console.MarkupLine(
                $"    failing  {Markup.Escape(failingVersion ?? "not given")}");
            _console.MarkupLine($"    HEAD     {Markup.Escape(headVersion)}");
            _console.MarkupLine($"    {Markup.Escape(baseBranch),-7}  {Markup.Escape(baseVersion)}");

            string alignment = ahead is null || behind is null
                ? "unknown"
                : $"{ahead.Value.ToString(CultureInfo.InvariantCulture)} ahead, " +
                  $"{behind.Value.ToString(CultureInfo.InvariantCulture)} behind";
            string subject = resumed is null ? "HEAD" : $"'{resumed}'";
            _console.MarkupLine(
                $"  server    {Markup.Escape(RunStartPlanner.Remote)}: {Markup.Escape(alignment)} " +
                $"against {Markup.Escape(baseBranch)}, for {Markup.Escape(subject)}");

            if (resumed is not null && headRevision is null)
            {
                _console.MarkupLine(
                    $"  [yellow]warn[/] '{Markup.Escape(resumed)}' has no commit yet, so it cannot be " +
                    "compared with the base.");
            }

            if (completedWarning is not null)
                _console.MarkupLine($"  [yellow]warn[/] {Markup.Escape(completedWarning)}");

            if (resumed is not null)
                WriteCompletedTasks(resumed, completed);
        });
    }

    /// <summary>
    ///     Prints each finished task with the report its commit carried, so a resumed run knows what the
    ///     branch already did without a pass over the plan that measures the same thing in minutes.
    /// </summary>
    /// <param name="branch">The branch whose commits were read.</param>
    /// <param name="completed">The tasks its trailer areas record, in the order the commits were made.</param>
    private void WriteCompletedTasks(string branch, IReadOnlyList<RunStartCompletedTask> completed)
    {
        if (completed.Count == 0)
        {
            _console.MarkupLine(
                $"  completed none — no commit on '{Markup.Escape(branch)}' ends with a 'Task:' line, so no " +
                "finished task is recorded there.");
            return;
        }

        _console.MarkupLine(
            $"  completed {completed.Count.ToString(CultureInfo.InvariantCulture)}");
        foreach (RunStartCompletedTask task in completed)
        {
            _console.MarkupLine($"    {Markup.Escape(task.Id)}");
            if (task.Report.Length == 0)
                continue;

            foreach (string line in task.Report.Split('\n'))
            {
                string text = line.TrimEnd('\r');
                _console.MarkupLine(text.Length == 0 ? string.Empty : $"      {Markup.Escape(text)}");
            }
        }
    }

    private int Refuse(string message)
    {
        _console.MarkupLine(
            $"[red]{Markup.Escape(CommandName)}: error:[/] {Markup.Escape(message)}");
        return RefusedInput;
    }

    private int Fail(string message)
    {
        _console.MarkupLine(
            $"[red]{Markup.Escape(CommandName)}: error:[/] {Markup.Escape(message)}");
        return ToolFailure;
    }

    private void WriteDiagnostics(DiagnosticReport report)
    {
        CommandHelpers.WriteUnfolded(_console, () =>
        {
            foreach (Diagnostic diagnostic in report.Items)
            {
                _console.MarkupLine(
                    $"[red]{Markup.Escape(diagnostic.Code)}[/] {Markup.Escape(diagnostic.Message)}");
                if (!string.IsNullOrEmpty(diagnostic.Hint))
                    _console.MarkupLine($"  [grey]→ {Markup.Escape(diagnostic.Hint)}[/]");
            }
        });
    }
}
