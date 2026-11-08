using System.Globalization;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.RunStart;
using Spectre.Console;
using Spectre.Console.Cli;

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
///         <c>stop-on-tool-failure</c>, <c>2</c> refused input, <c>3</c> the CodeGraph binary is missing.
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

    /// <summary>
    ///     The bound on one child. A fetch that reaches a slow remote is the only one expected to take real
    ///     time; a hung child must not hold the run open, which is why no call here is unbounded.
    /// </summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IProcessExecutor? _executor;

    /// <summary>Creates the command with the production process executor.</summary>
    public RunStartCommand()
    {
    }

    /// <summary>Creates the command with an injected process seam, for a caller that supplies its own.</summary>
    /// <param name="executor">The executor every git and CodeGraph child runs through.</param>
    internal RunStartCommand(IProcessExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
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

        IProcessExecutor executor = _executor ?? ProcessExecutor.WithTimeout(ToolTimeout);

        string? repositoryRoot = new RunStartRunner(executor, Environment.CurrentDirectory).DetectRepositoryRoot();
        if (repositoryRoot is null)
        {
            return Fail(
                $"'{Environment.CurrentDirectory}' is not inside a git repository, so there is no checkout " +
                "to fetch into or to create the branch in.");
        }

        RunStartRunner runner = new(executor, repositoryRoot);

        DiagnosticReport report = new();
        if (!CommandHelpers.TryLoadConfig(repositoryRoot, null, report, out KyberWeaveConfig config))
        {
            WriteDiagnostics(report);
            return ToolFailure;
        }

        if (!RunStartRunner.CodeGraphBinaryIsAvailable())
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] 'codegraph' is not on PATH, so the tools a " +
                "run depends on cannot be indexed or tested.");
            AnsiConsole.MarkupLine(
                "[grey]→ install the CodeGraph CLI so that 'codegraph' resolves on PATH, then start the run " +
                "again.[/]");
            return CodeGraphMissing;
        }

        GitOutcome fetch = runner.Fetch();
        if (!fetch.Succeeded)
        {
            if (config.ConfigReg.StopsOnToolFailure)
            {
                AnsiConsole.MarkupLine(
                    $"[red]{Markup.Escape(CommandName)}: error:[/] 'git fetch origin --prune' failed: " +
                    $"{Markup.Escape(fetch.FirstErrorLine)}");
                AnsiConsole.MarkupLine(
                    "[grey]→ a run that cannot refresh its base plans against stale history. Fix the remote, " +
                    "or set 'stop-on-tool-failure: false' in .kyber-weave/kyber-weave.yml to continue from " +
                    "the base this checkout last fetched.[/]");
                return ToolFailure;
            }

            AnsiConsole.MarkupLine(
                $"[yellow]warn[/] 'git fetch origin --prune' failed ({Markup.Escape(fetch.FirstErrorLine)}); " +
                "continuing from the base this checkout last fetched.");
        }

        string baseRef = RunStartPlanner.BaseRefFor(config.ConfigReg.RunBaseBranch);
        string? originHead = runner.OriginHeadSymbolicRef();
        string baseBranch = RunStartPlanner.BaseBranchName(baseRef, originHead);
        string? startRef = ResolveStartRef(runner, baseRef, originHead);
        if (startRef is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]{Markup.Escape(CommandName)}: error:[/] no ref for the base " +
                $"{Markup.Escape(baseRef)} resolves, so there is nothing to create the branch from.");
            AnsiConsole.MarkupLine(
                "[grey]→ set 'run.base-branch' in .kyber-weave/kyber-weave.yml to the branch the run is " +
                "compared against, or re-run once the fetch can reach origin.[/]");
            return ToolFailure;
        }

        string? resume = string.IsNullOrWhiteSpace(settings.Resume) ? null : settings.Resume.Trim();
        string resumeRef = string.Empty;
        string? resumeTarget = null;
        if (resume is not null)
        {
            string remotePrefix = RunStartPlanner.Remote + "/";
            bool isExplicitRemote = resume.StartsWith(remotePrefix, StringComparison.Ordinal);
            string stripped = isExplicitRemote
                ? resume[remotePrefix.Length..]
                : resume;

            if (string.Equals(stripped, "HEAD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resume, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(
                    $"'{resume}' is a symbolic reference, not a branch name. Pass the " +
                    "branch a plan or pull request already owns.");
            }

            if (isExplicitRemote)
            {
                if (runner.HasRemoteBranch(stripped))
                {
                    resumeRef = remotePrefix + stripped;
                    resumeTarget = stripped;
                }
                else
                {
                    return Refuse(
                        $"No local or origin branch is named '{resume}', so there is nothing to resume. Pass the " +
                        "branch a plan or pull request already owns.");
                }
            }
            else if (runner.HasLocalBranch(resume))
            {
                resumeRef = resume;
                resumeTarget = resume;
            }
            else if (runner.HasRemoteBranch(resume))
            {
                resumeRef = remotePrefix + resume;
                resumeTarget = resume;
            }
            else
            {
                return Refuse(
                    $"No local or origin branch is named '{resume}', so there is nothing to resume. Pass the " +
                    "branch a plan or pull request already owns.");
            }
        }

        // Read before anything is switched: the versions and the divergence describe the checkout as the
        // operator left it, which is the state a defect was observed in. A resumed branch is described by
        // its own tip, because that is what continuing on it means.
        string? headRevision = runner.ResolveRevision("HEAD");
        string? baseRevision = runner.ResolveRevision(startRef);
        string headVersion = runner.Describe("HEAD") ?? "unknown";
        string baseVersion = runner.Describe(startRef) ?? "unknown";
        string alignmentRef = resume is null ? "HEAD" : resumeRef;
        string? alignmentRevision = runner.ResolveRevision(alignmentRef);
        (int? ahead, int? behind) = runner.Divergence(alignmentRef, startRef);
        string? originUrl = runner.OriginAddress();

        GitOutcome status = runner.CheckoutStatus();
        if (!status.Succeeded)
        {
            return Fail($"git status failed: {status.FirstErrorLine}");
        }

        RunStartLocation location = RunStartPlanner.DecideLocation(
            status.StandardOutput.Trim().Length == 0,
            runner.AnotherWorktreeIsAttached());

        string target = resumeTarget ?? branch;
        string? worktreePath = null;
        GitOutcome created;
        if (location == RunStartLocation.InPlace)
        {
            created = resumeTarget is null
                ? runner.CreateBranchInPlace(target, startRef)
                : runner.SwitchToBranch(target);
        }
        else
        {
            worktreePath = runner.WorktreePathFor(target);
            created = resumeTarget is null
                ? runner.CreateBranchInWorktree(target, startRef, worktreePath)
                : runner.AddWorktreeForBranch(target, worktreePath, resumeRef);
        }

        if (!created.Succeeded)
        {
            return Fail(
                $"git could not put '{target}' in place: {created.FirstErrorLine}");
        }

        if (settings.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(
                new
                {
                    @base = baseBranch,
                    baseRef = startRef,
                    branch = target,
                    worktree = worktreePath,
                    location = location == RunStartLocation.InPlace ? "in-place" : "worktree",
                    revision = baseRevision,
                    resumed = resume is not null,
                    versions = new
                    {
                        failing = settings.FailingVersion,
                        head = headVersion,
                        @base = baseVersion
                    },
                    serverAlignment = new
                    {
                        remote = RunStartPlanner.Remote,
                        host = HostOf(originUrl),
                        remoteUrl = originUrl,
                        head = alignmentRevision,
                        ahead,
                        behind,
                        aligned = ahead is not null && behind is not null
                            ? ahead == 0 && behind == 0
                            : (bool?)null
                    }
                },
                JsonOptions));
            return Ready;
        }

        WriteReport(
            baseBranch,
            startRef,
            target,
            location,
            worktreePath,
            baseRevision,
            resumeTarget,
            settings.FailingVersion,
            headVersion,
            baseVersion,
            ahead,
            behind,
            headRevision);

        return Ready;
    }

    /// <summary>The two-argument overload the rest of this branch's commands carry.</summary>
    /// <param name="context">The command context Spectre supplies.</param>
    /// <param name="settings">The parsed settings.</param>
    public int Execute(CommandContext context, RunStartSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }

    /// <summary>
    ///     The first ref that resolves for the base: the configured branch, then the default branch, and the
    ///     local branch of that name as a last resort so a repository without remote-tracking refs still has
    ///     somewhere to start.
    /// </summary>
    private static string? ResolveStartRef(RunStartRunner runner, string baseRef, string? originHead)
    {
        foreach (string candidate in StartRefCandidates(baseRef, originHead))
        {
            if (runner.ResolveRevision(candidate) is not null)
                return candidate;
        }

        return null;
    }

    private static List<string> StartRefCandidates(string baseRef, string? originHead)
    {
        List<string> candidates = [];

        if (!string.Equals(baseRef, RunStartPlanner.RemoteHead, StringComparison.Ordinal))
        {
            candidates.Add(baseRef);
            candidates.Add(RunStartPlanner.BaseBranchName(baseRef, originHead));
            return candidates;
        }

        if (!string.IsNullOrWhiteSpace(originHead))
        {
            candidates.Add(originHead);
            candidates.Add(RunStartPlanner.BaseBranchName(originHead, null));
        }

        candidates.Add(RunStartPlanner.RemoteHead);
        return candidates;
    }

    /// <summary>
    ///     The host a remote URL names, or null when the URL is a local path this command cannot attribute to
    ///     a server.
    /// </summary>
    private static string? HostOf(string? remoteUrlText)
    {
        if (string.IsNullOrWhiteSpace(remoteUrlText))
            return null;

        string text = remoteUrlText.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? url) && !url.IsFile && url.Host.Length > 0)
            return url.Host;

        // The scp-like form git also accepts: [user@]host:path. It carries no scheme, so Uri cannot read it.
        int at = text.LastIndexOf('@');
        string afterUser = at >= 0 ? text[(at + 1)..] : text;
        int separator = afterUser.IndexOfAny([':', '/']);
        if (separator <= 0)
            return null;

        string host = afterUser[..separator];
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return host;

        // A bare path or a drive letter is not a server, and only a dotted name is read as one: reporting
        // "C" as the host a checkout is aligned with would be worse than reporting nothing.
        return host.Contains('.', StringComparison.Ordinal) ? host : null;
    }

    private static void WriteReport(
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
        string? headRevision)
    {
        string where = worktreePath is null ? "in place" : worktreePath;

        CommandHelpers.WriteUnfolded(() =>
        {
            AnsiConsole.MarkupLine(
                $"[bold]{Markup.Escape(CommandName)}[/] " +
                $"{(resumed is null ? "ready" : "resumed")}");
            AnsiConsole.MarkupLine(
                $"  base      {Markup.Escape(baseBranch)} ({Markup.Escape(startRef)})");
            AnsiConsole.MarkupLine($"  branch    {Markup.Escape(branch)}");
            AnsiConsole.MarkupLine($"  where     {Markup.Escape(where)}");
            AnsiConsole.MarkupLine($"  revision  {Markup.Escape(revision ?? "unknown")}");
            AnsiConsole.MarkupLine("  versions");
            AnsiConsole.MarkupLine(
                $"    failing  {Markup.Escape(failingVersion ?? "not given")}");
            AnsiConsole.MarkupLine($"    HEAD     {Markup.Escape(headVersion)}");
            AnsiConsole.MarkupLine($"    {Markup.Escape(baseBranch),-7}  {Markup.Escape(baseVersion)}");

            string alignment = ahead is null || behind is null
                ? "unknown"
                : $"{ahead.Value.ToString(CultureInfo.InvariantCulture)} ahead, " +
                  $"{behind.Value.ToString(CultureInfo.InvariantCulture)} behind";
            string subject = resumed is null ? "HEAD" : $"'{resumed}'";
            AnsiConsole.MarkupLine(
                $"  server    {Markup.Escape(RunStartPlanner.Remote)}: {Markup.Escape(alignment)} " +
                $"against {Markup.Escape(baseBranch)}, for {Markup.Escape(subject)}");

            if (resumed is not null && headRevision is null)
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]warn[/] '{Markup.Escape(resumed)}' has no commit yet, so it cannot be " +
                    "compared with the base.");
            }
        });
    }

    private static int Refuse(string message)
    {
        AnsiConsole.MarkupLine(
            $"[red]{Markup.Escape(CommandName)}: error:[/] {Markup.Escape(message)}");
        return RefusedInput;
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLine(
            $"[red]{Markup.Escape(CommandName)}: error:[/] {Markup.Escape(message)}");
        return ToolFailure;
    }

    private static void WriteDiagnostics(DiagnosticReport report)
    {
        CommandHelpers.WriteUnfolded(() =>
        {
            foreach (Diagnostic diagnostic in report.Items)
            {
                AnsiConsole.MarkupLine(
                    $"[red]{Markup.Escape(diagnostic.Code)}[/] {Markup.Escape(diagnostic.Message)}");
                if (!string.IsNullOrEmpty(diagnostic.Hint))
                    AnsiConsole.MarkupLine($"  [grey]→ {Markup.Escape(diagnostic.Hint)}[/]");
            }
        });
    }
}
