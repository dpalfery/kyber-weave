using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>
/// Delivers a finished run: commits the archived plan move and the filed todos if still pending, pushes the run branch, and creates
/// or updates its draft pull request on GitHub, with the title and description assembled from what the run recorded.
/// </summary>
/// <remarks>
/// It stays inside the approval autonomous mode gives a run: commits on the run branch, a push of that branch, a draft
/// pull request. It never force-pushes, pushes another branch, marks a pull request ready, merges, or requests
/// reviewers. Every local check runs before anything leaves the machine, and each refusal changes nothing. The
/// description goes to the provider on standard input, so no file is written anywhere.
/// </remarks>
public sealed partial class RunDelivery
{
    /// <summary>The request is unusable: no summary, or a title that breaks the branch's title convention.</summary>
    public const string InvalidRequest = "KW-DELIVER-001";

    /// <summary>The checkout is not on a run branch with a recorded base.</summary>
    public const string NotARunBranch = "KW-DELIVER-002";

    /// <summary>The working tree holds changes the deliver step does not own.</summary>
    public const string UncommittedChanges = "KW-DELIVER-003";

    /// <summary>The run's plan is not archived, or several archived plans and none named.</summary>
    public const string PlanNotDeliverable = "KW-DELIVER-004";

    /// <summary>The provider is not GitHub, or its CLI is missing or not signed in: the delivery agent takes over.</summary>
    public const string ProviderUnavailable = DeliveryOutcome.ProviderUnavailableCode;

    /// <summary>The push was refused.</summary>
    public const string PushFailed = "KW-DELIVER-006";

    /// <summary>The pull request could not be created, updated or read back.</summary>
    public const string PullRequestFailed = "KW-DELIVER-007";

    /// <summary>A step done.</summary>
    public const string Step = "KW-DELIVER-008";

    /// <summary>The longest summary accepted.</summary>
    public const int MaxSummaryLength = 1000;

    private const string Remote = "origin";

    /// <summary>
    /// The delivery agents a run may dispatch for the step that opens the pull request. Both hold the
    /// <c>publishing-worker</c> capability profile, so run start and delivery treat them alike.
    /// </summary>
    public const string DeliveryAgents = "devops";

    private const string DeliveryAgentHint =
        "Dispatch the delivery agent (" + DeliveryAgents + ") with the create-pull-request skill to open the pull request instead.";

    private static readonly IReadOnlyDictionary<string, string> TypeWords = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["feat"] = "Feature",
        ["fix"] = "Bug fix",
        ["docs"] = "Documentation",
        ["refactor"] = "Refactor",
        ["perf"] = "Performance",
        ["test"] = "Tests",
        ["build"] = "Build",
        ["ci"] = "Continuous integration",
        ["chore"] = "Maintenance",
        ["revert"] = "Revert",
        ["style"] = "Formatting",
    };

    private readonly string _repoRoot;
    private readonly IEmbeddedResources _resources;
    private readonly KyberWeaveConfig _config;
    private readonly IProcessRunner _executor;
    private readonly string _homeDirectory;
    private readonly ActivePlanFiles _files;
    private readonly IRepositoryFileStore _store;
    private readonly IRunStateStore _runState;
    private readonly RunDeliveryEvidence _evidence;

    /// <summary>
    /// Test seam, called with the absolute path of the archived plan once it is found and before it is read: the
    /// moment the plan can vanish between the search and the read.
    /// </summary>
    internal Action<string>? AfterPlanFound { get; init; }

    /// <summary>Test seam: reads a host pull request template file; null reads from disk.</summary>
    internal Func<string, string>? ReadTemplateFile { get; init; }

    /// <summary>Creates the deliver step for one checkout.</summary>
    /// <param name="repoRoot">The run's worktree.</param>
    /// <param name="config">The repository's effective configuration.</param>
    /// <param name="executor">Runs git and gh; null for the bounded default.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records; null for the user profile.</param>
    /// <param name="ports">The ports the run records, review evidence and plan files are read through.</param>
    public RunDelivery(string repoRoot, KyberWeaveConfig config, IProcessRunner? executor, string? homeDirectory, RunDeliveryPorts ports)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = ports.Paths.GetFullPath(repoRoot, ports.Environment.CurrentDirectory);
        _config = config;
        _executor = executor ?? new BoundedDeliveryProcessRunner(ports.Process, TimeSpan.FromMinutes(3));
        _homeDirectory = homeDirectory ?? ports.Environment.HomeDirectory;
        _store = ports.Files;
        _resources = ports.Resources;
        _runState = ports.RunState;
        _files = new ActivePlanFiles(_repoRoot, config, ports.Files, ports.Process);
        _evidence = new RunDeliveryEvidence(
            _repoRoot,
            config,
            _homeDirectory,
            _files,
            ports.Files,
            ports.Cycles,
            ports.Time,
            (arguments, cancellationToken) => GitAsync(cancellationToken, arguments));
    }

    /// <summary>Delivers the run, or refuses and changes nothing.</summary>
    public async Task<DeliveryOutcome> RunAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DiagnosticReport report = new();

        ResolvedRequest? resolved = await ResolveRequestAsync(request, report, cancellationToken).ConfigureAwait(false);
        if (resolved is null)
            return new DeliveryOutcome(report, false);

        string summary = resolved.Summary;
        string branch = resolved.Branch;
        string harness = resolved.Harness;
        string type = resolved.Type;
        string slug = resolved.Slug;
        string baseBranch = resolved.BaseBranch;
        string baseRef = resolved.BaseRef;

        string? repository = await RejectNonGitHubOriginAsync(report, cancellationToken).ConfigureAwait(false);
        if (repository is null)
            return new DeliveryOutcome(report, false);

        WorktreeState? worktree = await RejectDirtyWorktreeAsync(report, cancellationToken).ConfigureAwait(false);
        if (worktree is null)
            return new DeliveryOutcome(report, false);

        string mergeBase = (await GitAsync(cancellationToken, "merge-base", baseRef, "HEAD").ConfigureAwait(false)).StandardOutput.Trim();
        if (mergeBase.Length == 0)
            return Refused(report, NotARunBranch, $"'{branch}' shares no history with its base '{baseRef}'. Nothing was changed.");

        PlanDiscovery? discovery = await TryFindPlanAsync(request.PlanFile, mergeBase, worktree.PlansFolder, worktree.ArchiveFolder, worktree.StatusOutput, report, cancellationToken).ConfigureAwait(false);
        if (discovery is null)
            return new DeliveryOutcome(report, false);

        string? plan = discovery.Plan;
        if (plan is not null)
            AfterPlanFound?.Invoke(_files.Absolute(plan));

        PlanFacts? facts = ReadPlanFacts(plan, report);
        if (plan is not null && facts is null)
            return new DeliveryOutcome(report, false);

        string title = Title(request.Title, facts, harness, type, slug);
        if (!TitleFollowsConvention(title, harness))
        {
            return Refused(report, InvalidRequest,
                $"The title '{title}' does not follow <type>(<scope>): <summary> [{harness}], with the harness once, at the end. Nothing was changed.",
                "Leave the title out to have it composed, or fix the override.");
        }

        if (await CheckGitHubCliAsync(report, cancellationToken).ConfigureAwait(false) is not null)
            return new DeliveryOutcome(report, false);

        if (worktree.PendingPlanFiles.Count > 0 && !await CommitPlanFilesAsync([.. new[] { worktree.PlansFolder, worktree.ArchiveFolder }.Where(folder => _store.DirectoryExists(_files.Absolute(folder)))], plan, report, cancellationToken).ConfigureAwait(false))
            return new DeliveryOutcome(report, false);
        if (worktree.PendingTodoFiles.Count > 0 && !await CommitTodoFilesAsync(worktree.TodoFolder, report, cancellationToken).ConfigureAwait(false))
            return new DeliveryOutcome(report, false);

        string body = await PreparePullRequestAsync(request, summary, facts, plan, worktree.TodoFolder, type, slug, branch, mergeBase, report, cancellationToken).ConfigureAwait(false);

        ProcessResult push = await GitAsync(cancellationToken, "push", "--porcelain", Remote, $"refs/heads/{branch}:refs/heads/{branch}").ConfigureAwait(false);
        if (push.ExitCode != 0)
        {
            return Refused(report, PushFailed, $"git push of '{branch}' was refused: {FirstLine(push.StandardError + push.StandardOutput)}.",
                "Never force it. If the remote branch has commits this one lacks, a person decides how to reconcile them.");
        }

        Say(report, $"Pushed {branch} to {Remote}.");
        return await PullRequestAsync(report, repository, branch, baseBranch, title, body, request.Draft, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the request to the run branch it names, refusing a missing or overlong summary, a
    /// branch that is not a run branch, a branch with no recorded base, and a branch that is its
    /// own base or the default branch. The refusal is recorded in the report; null answers when it
    /// refuses, the resolved parts otherwise.
    /// </summary>
    private async Task<ResolvedRequest?> ResolveRequestAsync(DeliveryRequest request, DiagnosticReport report, CancellationToken cancellationToken)
    {
        string summary = request.Summary?.Trim() ?? string.Empty;
        if (summary.Length == 0)
        {
            Refused(report, InvalidRequest, "No summary was given. Nothing was changed.", "Pass one to three plain sentences: the problem and the outcome.");
            return null;
        }
        if (summary.Length > MaxSummaryLength)
        {
            Refused(report, InvalidRequest, $"The summary is longer than {MaxSummaryLength} characters. Nothing was changed.", "Keep it to one to three sentences.");
            return null;
        }

        string branch = (await GitAsync(cancellationToken, "symbolic-ref", "--quiet", "--short", "HEAD").ConfigureAwait(false)).StandardOutput.Trim();
        if (branch.Length == 0)
        {
            Refused(report, NotARunBranch, "The checkout is not on a branch (detached HEAD). Nothing was changed.", "Call this from the run's worktree, on the run branch.");
            return null;
        }
        if (!TryParseRunBranch(branch, out string harness, out string type, out string slug))
        {
            Refused(report, NotARunBranch,
                $"'{branch}' is not a run branch: run branches are named [<owner>.<agent>.]<harness>/<type>/<slug>. Nothing was changed.",
                "Deliver only a branch that kyber-weave run start created.");
            return null;
        }

        if (!_runState.TryReadBaseRef(_homeDirectory, branch, out string? baseRefValue) || baseRefValue is null)
        {
            Refused(report, NotARunBranch, $"No run record names the base '{branch}' was started from. Nothing was changed.",
                "Deliver only a branch kyber-weave run start created or resumed (--resume <branch>), which records its base.");
            return null;
        }

        string? originHead = (await GitAsync(cancellationToken, "symbolic-ref", "--quiet", "--short", $"refs/remotes/{Remote}/HEAD").ConfigureAwait(false)) is { ExitCode: 0 } head
            ? head.StandardOutput.Trim()
            : null;
        string baseBranch = RunStartPlanner.BaseBranchName(baseRefValue, originHead);
        string? defaultBranch = originHead is null ? null : RunStartPlanner.BaseBranchName(originHead, null);
        if (string.Equals(branch, baseBranch, StringComparison.Ordinal) || string.Equals(branch, defaultBranch, StringComparison.Ordinal)
            || string.Equals(baseBranch, "HEAD", StringComparison.Ordinal))
        {
            Refused(report, NotARunBranch, $"'{branch}' is the base or default branch, or its base cannot be named. Nothing was changed.",
                "Deliver only the run's own branch, never the branch it merges into.");
            return null;
        }

        return new ResolvedRequest(summary, branch, harness, type, slug, baseBranch, baseRefValue);
    }

    /// <summary>
    /// Refuses an origin remote that is not a github.com one — the step opens pull requests on
    /// github.com only. Null answers when it refuses, the owner and repository otherwise.
    /// </summary>
    private async Task<string?> RejectNonGitHubOriginAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        string remoteUrl = (await GitAsync(cancellationToken, "remote", "get-url", Remote).ConfigureAwait(false)).StandardOutput.Trim();
        if (GitHubRepository(remoteUrl) is not { } found)
        {
            string host = remoteUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) || remoteUrl.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase)
                ? "Azure DevOps"
                : remoteUrl.Length == 0 ? "no origin remote" : "a host other than github.com";
            Refused(report, ProviderUnavailable, $"The origin remote is {host}; this step opens pull requests on github.com only. Nothing was changed.", DeliveryAgentHint);
            return null;
        }

        return found;
    }

    /// <summary>
    /// Classifies the uncommitted changes: the plan and todo folders may hold pending changes the
    /// delivery commits, but anything outside them is someone else's work and refuses. Null answers
    /// when it refuses, the classified state otherwise.
    /// </summary>
    private async Task<WorktreeState?> RejectDirtyWorktreeAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        ProcessResult status = await GitAsync(cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all").ConfigureAwait(false);
        if (status.ExitCode != 0)
        {
            Refused(report, UncommittedChanges, $"git status failed: {FirstLine(status.StandardError)}. Nothing was changed.");
            return null;
        }

        (string folderPlans, string folderArchive) = PlanFolders();
        string folderTodo = TodoFolder();
        string statusText = status.StandardOutput;
        List<string> dirty = [.. ChangedPaths(statusText).Where(path => !IsUnder(path, ScratchLayout.Root))];
        List<string> folderPendingPlans = [.. dirty.Where(path => IsUnder(path, folderPlans) || IsUnder(path, folderArchive))];
        List<string> folderPendingTodos = [.. dirty.Where(path => IsUnder(path, folderTodo))];
        List<string> foreign = [.. dirty.Except(folderPendingPlans, StringComparer.Ordinal).Except(folderPendingTodos, StringComparer.Ordinal)];
        if (foreign.Count > 0)
        {
            Refused(report, UncommittedChanges,
                $"Uncommitted changes outside the plan and todo files: {string.Join(", ", foreign.Take(5))}{(foreign.Count > 5 ? $" and {foreign.Count - 5} more" : string.Empty)}. Nothing was changed.",
                "Each worker commits its own changes. Send these to the worker that owns them, or restore files a tool touched by mistake, then call this again.");
            return null;
        }

        return new WorktreeState(folderPlans, folderArchive, folderTodo, folderPendingPlans, folderPendingTodos, statusText);
    }

    /// <summary>
    /// The plan's facts from its text. Null when there is no plan, and — with a refusal recorded —
    /// when the plan cannot be read or is no longer there.
    /// </summary>
    private PlanFacts? ReadPlanFacts(string? plan, DiagnosticReport report)
    {
        if (plan is null)
            return null;

        try
        {
            string? planText = _store.ReadTextIfPresent(_files.Absolute(plan));
            if (planText is null)
            {
                Refused(report, PlanNotDeliverable, $"The plan '{plan}' is no longer there. Nothing was changed.");
                return null;
            }

            return RunEvidence.ReadPlan(planText);
        }
        catch (Exception exception) when (FileFailure.Is(exception))
        {
            Refused(report, PlanNotDeliverable, $"The plan '{plan}' could not be read: {exception.Message}. Nothing was changed.",
                "Make the archived plan a readable file, then call this again.");
            return null;
        }
    }

    /// <summary>Refuses when the GitHub CLI cannot be run or is not signed in to github.com.</summary>
    private async Task<DeliveryOutcome?> CheckGitHubCliAsync(DiagnosticReport report, CancellationToken cancellationToken)
    {
        if ((await GhAsync(cancellationToken, "--version").ConfigureAwait(false)) is { ExitCode: not 0 } version)
            return Refused(report, ProviderUnavailable, $"The GitHub CLI (gh) could not be run: {FirstLine(version.StandardError)}. Nothing was changed.", DeliveryAgentHint);
        if ((await GhAsync(cancellationToken, "auth", "status", "--hostname", "github.com").ConfigureAwait(false)) is { ExitCode: not 0 } auth)
            return Refused(report, ProviderUnavailable, $"The GitHub CLI is not signed in to github.com: {FirstLine(auth.StandardError + auth.StandardOutput)}. Nothing was changed.", DeliveryAgentHint);

        return null;
    }

    /// <summary>
    /// The pull request description: the run's content with its filed todos, rendered against the
    /// host's template, naming the templates it could not read.
    /// </summary>
    private async Task<string> PreparePullRequestAsync(
        DeliveryRequest request,
        string summary,
        PlanFacts? facts,
        string? plan,
        string todoFolder,
        string type,
        string slug,
        string branch,
        string mergeBase,
        DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        PullRequestContent content = (await ContentAsync(request, summary, facts, plan, type, slug, branch, mergeBase, report, cancellationToken).ConfigureAwait(false)) with
        {
            FollowUps = await _evidence.FiledTodosAsync(mergeBase, todoFolder, report, cancellationToken).ConfigureAwait(false)
        };
        PullRequestDescription.HostTemplate hostTemplate = PullRequestDescription.ReadHostTemplate(_store, _repoRoot, ReadTemplateFile ?? _store.ReadAllTextBounded);
        foreach (string unreadable in hostTemplate.Unreadable)
        {
            report.Add(new Diagnostic(Step, Severity.Warning,
                $"The pull request template '{unreadable}' could not be read, so it was not used{(hostTemplate.Text is null ? "; the standard description format was used instead" : "; the next template found was used instead")}.", "deliver"));
        }

        return PullRequestDescription.Render(_resources, content, hostTemplate.Text);
    }

    private DeliveryOutcome Refused(DiagnosticReport report, string code, string message, string? hint = null)
    {
        report.Add(new Diagnostic(code, Severity.Error, message, "deliver", Hint: hint));
        return new DeliveryOutcome(report, false);
    }

    /// <summary>Splits a run branch, <c>[&lt;owner&gt;.&lt;agent&gt;.]&lt;harness&gt;/&lt;type&gt;/&lt;slug&gt;</c>, into its parts.</summary>
    public static bool TryParseRunBranch(string branch, out string harness, out string type, out string slug)
    {
        ArgumentNullException.ThrowIfNull(branch);
        harness = type = slug = string.Empty;
        string[] parts = branch.Split('/', 3);
        if (parts.Length != 3)
            return false;

        string first = parts[0];
        string token = first[(first.LastIndexOf('.') + 1)..];
        if (!RunStartPlanner.TryResolveHarness(token, out string canonical) || !RunStartPlanner.IsConventionalType(parts[1])
                                                                          || !RunStartPlanner.IsSlug(parts[2]))
            return false;

        harness = canonical;
        type = parts[1];
        slug = parts[2];
        return true;
    }

    /// <summary>Whether a title is <c>&lt;type&gt;[(&lt;scope&gt;)][!]: &lt;summary&gt; [&lt;harness&gt;]</c>, with the harness once, at the end.</summary>
    public static bool TitleFollowsConvention(string title, string harness)
    {
        ArgumentNullException.ThrowIfNull(title);
        string suffix = $" [{harness}]";
        return ConventionalTitle().IsMatch(title)
               && title.EndsWith(suffix, StringComparison.Ordinal)
               && BracketGroup().Count(title) == 1;
    }

    /// <summary>The composed title: the type and scope, the plan's title or the slug in words, and the harness.</summary>
    internal static string Title(string? overrideTitle, PlanFacts? facts, string harness, string type, string slug)
    {
        if (!string.IsNullOrWhiteSpace(overrideTitle))
            return overrideTitle.Trim();

#pragma warning disable CA1308 // A conventional-commit scope is lowercase by convention; nothing compares it.
        string scope = facts?.Component is { Length: > 0 } component
            ? $"({ScopeWord().Replace(component.ToLowerInvariant(), "-").Trim('-')})"
            : string.Empty;
#pragma warning restore CA1308
        string words = facts?.Title is { Length: > 0 } planTitle ? planTitle : slug.Replace('-', ' ');
        words = words.Trim().TrimEnd('.');
        if (words.Length > 1 && !char.IsUpper(words[1]))
            words = char.ToLowerInvariant(words[0]) + words[1..];
        return $"{type}{scope}: {words} [{harness}]";
    }

    private async Task<PullRequestContent> ContentAsync(DeliveryRequest request, string summary, PlanFacts? facts, string? plan, string type, string slug, string branch, string mergeBase, DiagnosticReport report, CancellationToken cancellationToken)
    {
        List<string> validation = [];
        List<string> notVerified = [.. (facts?.NotVerified ?? []).Select(item => char.ToUpperInvariant(item[0]) + item[1..])];

        IReadOnlyList<string> pending = await _evidence.CollectValidationAsync(validation, notVerified, branch, mergeBase, report, cancellationToken).ConfigureAwait(false);

        List<string> decisions = [.. facts?.LowConfidenceDecisions ?? []];
        if (decisions.Count > 0 && facts is { } knownFacts && knownFacts.AcceptedRecommendations > 0)
            decisions.Add(knownFacts.AcceptedRecommendations == 1
                ? "One other decision followed the planner's recommendation; the plan lists it."
                : $"{knownFacts.AcceptedRecommendations.ToString(CultureInfo.InvariantCulture)} other decisions followed the planner's recommendation; the plan lists them.");

        List<string> notes = [];
        if (!string.IsNullOrWhiteSpace(request.Notes))
            notes.Add(request.Notes.Trim());
        notes.Add(plan is null
            ? "No plan was archived on this branch."
            : $"The design and every decision are in the archived plan, `{plan}`.");

        Match number = LeadingNumber().Match(slug);
        return new PullRequestContent
        {
            Summary = summary,
            Type = TypeWords.GetValueOrDefault(type, type),
            LinkedWork = number.Success ? $"Part of #{number.Groups[1].Value}" : null,
            Changes = facts?.Changes ?? [],
            Validation = validation,
            NotVerified = notVerified,
            Decisions = decisions,
            PendingReview = pending,
            DocumentationImpact = request.DocumentationImpact,
            Notes = string.Join("\n\n", notes),
        };
    }

    private async Task<DeliveryOutcome> PullRequestAsync(DiagnosticReport report, string repository, string branch, string baseBranch, string title, string body, bool draft, CancellationToken cancellationToken)
    {
        DeliveryOutcome Failed(string message)
        {
            report.Add(new Diagnostic(PullRequestFailed, Severity.Error, $"{message} Branch '{branch}' is already pushed.", "deliver",
                Hint: "The branch is pushed; calling this again creates or updates the pull request without pushing anything new."));
            return new DeliveryOutcome(report, false, Head: branch, Base: baseBranch, Title: title, Body: body);
        }

        ProcessResult list = await GhAsync(cancellationToken, "pr", "list", "--repo", repository, "--head", branch, "--state", "open", "--json", "number,url,baseRefName").ConfigureAwait(false);
        if (list.ExitCode != 0)
            return Failed($"gh pr list failed: {FirstLine(list.StandardError)}.");

        JsonElement? existing = null;
        try
        {
            using JsonDocument found = JsonDocument.Parse(list.StandardOutput.Length == 0 ? "[]" : list.StandardOutput);
            if (found.RootElement.ValueKind == JsonValueKind.Array && found.RootElement.GetArrayLength() > 0)
                existing = found.RootElement[0].Clone();
        }
        catch (JsonException exception)
        {
            return Failed($"gh pr list answered something that is not JSON: {exception.Message}");
        }

        bool created;
        string target;
        if (existing is { } open)
        {
            if (!GhJson.TryRequiredNumber(open, "number", out int openNumber, out string? listProblem)
                || !GhJson.TryRequiredText(open, "baseRefName", out string openBase, out listProblem))
            {
                return Failed($"gh pr list answered an open pull request with an unusable field: {listProblem}.");
            }

            string number = openNumber.ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(openBase, baseBranch, StringComparison.Ordinal))
                return Failed($"Pull request #{number} from '{branch}' is open into '{openBase}', not the run's base '{baseBranch}'. A person decides which base is right; it was not changed.");

            ProcessResult edit = await GhWithInputAsync(body, ["pr", "edit", number, "--repo", repository, "--title", title, "--body-file", "-"], cancellationToken).ConfigureAwait(false);
            if (edit.ExitCode != 0)
                return Failed($"gh pr edit #{number} failed: {FirstLine(edit.StandardError)}.");
            created = false;
            target = number;
            Say(report, $"Updated the title and description of open pull request #{number}.");
        }
        else
        {
            List<string> arguments = ["pr", "create", "--repo", repository, "--head", branch, "--base", baseBranch, "--title", title, "--body-file", "-", "--assignee", "@me"];
            if (draft)
                arguments.Add("--draft");
            ProcessResult create = await GhWithInputAsync(body, arguments, cancellationToken).ConfigureAwait(false);
            if (create.ExitCode != 0)
                return Failed($"gh pr create failed: {FirstLine(create.StandardError)}.");
            created = true;
            target = create.StandardOutput.Trim().Split('\n')[^1].Trim();
            Say(report, $"Created {(draft ? "a draft" : "a")} pull request from {branch} into {baseBranch}, assigned to the signed-in user.");
        }

        ProcessResult view = await GhAsync(cancellationToken, "pr", "view", target, "--repo", repository, "--json", "number,url,isDraft,baseRefName,headRefName,title,body,statusCheckRollup").ConfigureAwait(false);
        if (view.ExitCode != 0)
            return Failed($"gh pr view failed after the {(created ? "create" : "update")}: {FirstLine(view.StandardError)}.");

        try
        {
            using JsonDocument read = JsonDocument.Parse(view.StandardOutput);
            JsonElement pr = read.RootElement;
            if (!GhJson.TryRequiredNumber(pr, "number", out int prNumber, out string? viewProblem)
                || !GhJson.TryRequiredBoolean(pr, "isDraft", out bool readDraft, out viewProblem)
                || !GhJson.TryRequiredText(pr, "url", out string url, out viewProblem)
                || !GhJson.TryRequiredText(pr, "baseRefName", out string readBase, out viewProblem)
                || !GhJson.TryRequiredText(pr, "headRefName", out string readHead, out viewProblem)
                || !GhJson.TryRequiredText(pr, "title", out string readTitle, out viewProblem)
                || !GhJson.TryRequiredString(pr, "body", out string readBody, out viewProblem))
            {
                return Failed($"gh pr view answered with an unusable field: {viewProblem}.");
            }

            if (!string.Equals(readTitle, title, StringComparison.Ordinal) || !string.Equals(Normalized(readBody), Normalized(body), StringComparison.Ordinal))
            {
                report.Add(new Diagnostic(Step, Severity.Warning,
                    "The pull request read back with a different title or description than was sent; check it on the page.", "deliver"));
            }

            return new DeliveryOutcome(report, true, created,
                url,
                prNumber,
                readDraft,
                readBase,
                readHead,
                Checks(pr.TryGetProperty("statusCheckRollup", out JsonElement rollup) ? rollup : default),
                title,
                body);
        }
        catch (JsonException exception)
        {
            return Failed($"gh pr view answered in an unexpected shape: {exception.Message}");
        }
    }

    /// <summary>One line on the CI checks GitHub reports for the pull request.</summary>
    internal static string Checks(JsonElement rollup)
    {
        if (rollup.ValueKind != JsonValueKind.Array || rollup.GetArrayLength() == 0)
            return "no CI checks reported yet";

        int pending = 0, failed = 0, passed = 0;
        foreach (JsonElement check in rollup.EnumerateArray())
        {
            string state = (GhJson.OptionalText(check, "conclusion") is { Length: > 0 } conclusion ? conclusion : GhJson.OptionalText(check, "state") ?? string.Empty).ToUpperInvariant();
            string progress = (GhJson.OptionalText(check, "status") ?? "COMPLETED").ToUpperInvariant();
            if (progress != "COMPLETED" || state is "" or "PENDING" or "EXPECTED")
                pending++;
            else if (state is "SUCCESS" or "NEUTRAL" or "SKIPPED")
                passed++;
            else
                failed++;
        }

        return $"{rollup.GetArrayLength().ToString(CultureInfo.InvariantCulture)} CI checks reported: {passed} passed, {failed} failed, {pending} pending";
    }

    private async Task<PlanDiscovery?> TryFindPlanAsync(string? named, string mergeBase, string plansFolder, string archiveFolder, string porcelain, DiagnosticReport report, CancellationToken cancellationToken)
    {
        List<string> added = [.. (await GitAsync(cancellationToken, "diff", "--name-only", "--diff-filter=A", "-z", mergeBase, "HEAD", "--").ConfigureAwait(false))
            .StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        added.AddRange(AddedInWorkingTree(porcelain));

        List<string> active = [.. added.Where(path => IsPlanFile(path, plansFolder) && _store.FileExists(_files.Absolute(path))).Distinct(StringComparer.Ordinal)];
        if (active.Count > 0)
        {
            report.Add(new Diagnostic(PlanNotDeliverable, Severity.Error,
                $"The plan this branch added is not archived yet: {string.Join(", ", active)}. Nothing was changed.", "deliver",
                Hint: "Archive it first with the archive tool, then call this again."));
            return null;
        }

        List<string> archived = [.. added.Where(path => IsPlanFile(path, archiveFolder) && _store.FileExists(_files.Absolute(path))).Distinct(StringComparer.Ordinal)];
        if (!string.IsNullOrWhiteSpace(named))
        {
            string wanted = Path.GetFileName(named.Trim().Replace('\\', '/'));
            string? namedPlan = archived.FirstOrDefault(path => string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(Path.GetFileNameWithoutExtension(path), wanted, StringComparison.OrdinalIgnoreCase));
            if (namedPlan is not null)
                return new PlanDiscovery(namedPlan);
            report.Add(new Diagnostic(PlanNotDeliverable, Severity.Error,
                $"'{named}' is not a plan this branch archived ({(archived.Count == 0 ? "it archived none" : string.Join(", ", archived))}). Nothing was changed.", "deliver"));
            return null;
        }

        if (archived.Count > 1)
        {
            report.Add(new Diagnostic(PlanNotDeliverable, Severity.Error,
                $"This branch archived several plans ({string.Join(", ", archived)}). Nothing was changed.", "deliver",
                Hint: "Name the run's plan with planFile."));
            return null;
        }

        return new PlanDiscovery(archived.Count == 1 ? archived[0] : null);
    }

    /// <summary>Commits the plan folders, which hold every pending change once the other paths were found clean.</summary>
    private async Task<bool> CommitPlanFilesAsync(IReadOnlyList<string> paths, string? plan, DiagnosticReport report, CancellationToken cancellationToken)
    {
        string name = plan is null ? "plan" : DatePrefix().Replace(Path.GetFileNameWithoutExtension(plan), string.Empty) + " plan";
        string message = $"docs(plan): archive {name}";
        ProcessResult add = await GitAsync(cancellationToken, ["add", "-A", "--", .. paths]).ConfigureAwait(false);
        ProcessResult commit = add.ExitCode == 0 ? await GitAsync(cancellationToken, ["commit", "-m", message, "--", .. paths]).ConfigureAwait(false) : add;
        if (commit.ExitCode != 0)
        {
            report.Add(new Diagnostic(UncommittedChanges, Severity.Error,
                $"Committing the pending plan files failed: {FirstLine(commit.StandardError + commit.StandardOutput)}. Nothing was pushed.", "deliver",
                Hint: "The files may be left staged; commit them, then call this again."));
            return false;
        }

        Say(report, $"Committed the pending plan files on the run branch: {message}.");
        return true;
    }

    /// <summary>The folder the <c>todo-index</c> registry entry lives in, or empty when the host declares none.</summary>
    private string TodoFolder()
    {
        string? index = _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(entry => string.Equals(entry.Name, ConfigRegConfig.TodoIndexProperty, StringComparison.Ordinal))
            ?.Path;
        return index is null ? string.Empty : Path.GetDirectoryName(index)?.Replace('\\', '/') ?? string.Empty;
    }

    /// <summary>Commits the todo folder, which the follow-up filing wrote after the review and nobody committed.</summary>
    private async Task<bool> CommitTodoFilesAsync(string todoFolder, DiagnosticReport report, CancellationToken cancellationToken)
    {
        const string message = "docs(todo): file the follow-up work this run found";
        ProcessResult add = await GitAsync(cancellationToken, "add", "-A", "--", todoFolder).ConfigureAwait(false);
        ProcessResult commit = add.ExitCode == 0 ? await GitAsync(cancellationToken, "commit", "-m", message, "--", todoFolder).ConfigureAwait(false) : add;
        if (commit.ExitCode != 0)
        {
            report.Add(new Diagnostic(UncommittedChanges, Severity.Error,
                $"Committing the pending todo files failed: {FirstLine(commit.StandardError + commit.StandardOutput)}. Nothing was pushed.", "deliver",
                Hint: "The files may be left staged; commit them, then call this again."));
            return false;
        }

        Say(report, $"Committed the pending todo files on the run branch: {message}.");
        return true;
    }

    private (string Plans, string Archive) PlanFolders()
    {
        string index = _files.PlanIndexPath() ?? string.Empty;
        string plans = Path.GetDirectoryName(index)?.Replace('\\', '/') ?? string.Empty;
        string root = _files.DocsRootOf(index);
        string archive = string.Join('/', new[] { root, OntologyConfig.ArchiveSegment, DocsLayout.Plans }.Where(part => part.Length > 0));
        return (plans, archive);
    }

    private static bool IsUnder(string path, string folder) =>
        folder.Length > 0 && path.StartsWith(folder + "/", StringComparison.Ordinal);

    internal static bool IsPlanFile(string path, string folder) =>
        IsUnder(path, folder) && !path[(folder.Length + 1)..].Contains('/', StringComparison.Ordinal)
                              && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(Path.GetFileName(path), "README.md", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every path <c>git status --porcelain=v1 -z</c> names, both sides of a rename included.</summary>
    internal static List<string> ChangedPaths(string porcelain)
    {
        List<string> paths = [];
        string[] fields = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < fields.Length; index++)
        {
            string field = fields[index];
            if (field.Length < 4)
                continue;
            paths.Add(field[3..]);
            if (field[0] is 'R' or 'C' && index + 1 < fields.Length)
                paths.Add(fields[++index]);
        }

        return paths;
    }

    private static IEnumerable<string> AddedInWorkingTree(string porcelain)
    {
        string[] fields = porcelain.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < fields.Length; index++)
        {
            string field = fields[index];
            if (field.Length < 4)
                continue;
            if (field[0] is 'A' or 'R' or 'C' || field.StartsWith("??", StringComparison.Ordinal))
                yield return field[3..];
            if (field[0] is 'R' or 'C')
                index++;
        }
    }

    /// <summary>The <c>owner/repo</c> of a github.com remote, or null for any other host.</summary>
    internal static string? GitHubRepository(string remoteUrl)
    {
        Match match = GitHubRemote().Match(remoteUrl.Trim());
        if (!match.Success)
            return null;
        string repo = match.Groups["repo"].Value;
        if (repo.EndsWith(".git", StringComparison.Ordinal))
            repo = repo[..^4];
        return repo.Length == 0 ? null : $"{match.Groups["owner"].Value}/{repo}";
    }

    private static string Normalized(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();

    private static string FirstLine(string text)
    {
        string trimmed = text.Trim();
        int newline = trimmed.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? trimmed : trimmed[..newline].Trim();
    }

    private static void Say(DiagnosticReport report, string message) =>
        report.Add(new Diagnostic(Step, Severity.Info, message, "deliver"));

    private Task<ProcessResult> GitAsync(CancellationToken cancellationToken, params string[] arguments) => StartAsync("git", string.Empty, arguments, cancellationToken);

    private Task<ProcessResult> GhAsync(CancellationToken cancellationToken, params string[] arguments) => StartAsync("gh", string.Empty, arguments, cancellationToken);

    private Task<ProcessResult> GhWithInputAsync(string standardInput, IReadOnlyList<string> arguments, CancellationToken cancellationToken) => StartAsync("gh", standardInput, arguments, cancellationToken);

    private async Task<ProcessResult> StartAsync(string program, string standardInput, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(program)
        {
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            return await _executor.RunAsync(startInfo, standardInput, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, $"{program} could not be run: {exception.Message}");
        }
    }

    /// <summary>The request resolved to the run branch it names.</summary>
    /// <param name="Summary">The trimmed summary that describes the change.</param>
    /// <param name="Branch">The run branch the checkout is on.</param>
    /// <param name="Harness">The harness word of the branch name.</param>
    /// <param name="Type">The type word of the branch name.</param>
    /// <param name="Slug">The slug of the branch name.</param>
    /// <param name="BaseBranch">The base branch by its conventional name.</param>
    /// <param name="BaseRef">The ref the run record names as the branch's base.</param>
    private sealed record ResolvedRequest(string Summary, string Branch, string Harness, string Type, string Slug, string BaseBranch, string BaseRef);

    /// <summary>The classified state of a worktree that holds only changes the delivery owns.</summary>
    /// <param name="PlansFolder">The folder active plans live in.</param>
    /// <param name="ArchiveFolder">The folder archived plans live in.</param>
    /// <param name="TodoFolder">The folder filed todos live in, empty when the host declares none.</param>
    /// <param name="PendingPlanFiles">The plan files with uncommitted changes the delivery commits.</param>
    /// <param name="PendingTodoFiles">The todo files with uncommitted changes the delivery commits.</param>
    /// <param name="StatusOutput">The status output plan discovery reads.</param>
    private sealed record WorktreeState(string PlansFolder, string ArchiveFolder, string TodoFolder, IReadOnlyList<string> PendingPlanFiles, IReadOnlyList<string> PendingTodoFiles, string StatusOutput);

    /// <summary>The archived plan the branch delivers, or none when it archived no plan.</summary>
    /// <param name="Plan">The path of the plan to deliver, or null when the branch archived none.</param>
    private sealed record PlanDiscovery(string? Plan);

    [GeneratedRegex(@"^[a-z]+(?:\([^()\s][^()]*\))?!?: \S", RegexOptions.CultureInvariant)]
    private static partial Regex ConventionalTitle();

    [GeneratedRegex(@"\[[^\[\]]+\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex ScopeWord();

    [GeneratedRegex(@"^(\d+)-", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}-", RegexOptions.CultureInvariant)]
    private static partial Regex DatePrefix();

    [GeneratedRegex(@"^(?:git@github\.com:|https?://(?:[^/@]+@)?github\.com/|ssh://git@github\.com/)(?<owner>[^/]+)/(?<repo>[^/]+?)/?$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRemote();
}
