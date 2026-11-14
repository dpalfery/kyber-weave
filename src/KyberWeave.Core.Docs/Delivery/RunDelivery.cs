using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Core.Docs.Delivery;

/// <summary>What the conductor asks the deliver step for.</summary>
/// <param name="Summary">The problem and the outcome, in one to three plain sentences.</param>
/// <param name="Notes">Extra notes for reviewers, or null.</param>
/// <param name="Title">A title that replaces the composed one, or null.</param>
/// <param name="Draft">Whether a new pull request is opened as a draft.</param>
/// <param name="DocumentationImpact">The documentation-impact outcome as docs-dev returned it, or null.</param>
/// <param name="PlanFile">The archived plan, when the branch archived more than one; null to find it.</param>
public sealed record DeliveryRequest(
    string Summary,
    string? Notes = null,
    string? Title = null,
    bool Draft = true,
    string? DocumentationImpact = null,
    string? PlanFile = null);

/// <summary>What a delivery did.</summary>
/// <param name="Report">The findings, and one Info line per step taken.</param>
/// <param name="Delivered">Whether the branch was pushed and its pull request created or updated.</param>
/// <param name="Created">True when this call created the pull request, false when it updated an open one.</param>
/// <param name="Link">The pull request's address.</param>
/// <param name="Number">The pull request's number.</param>
/// <param name="IsDraft">Whether the pull request is a draft, as read back.</param>
/// <param name="Base">The branch it merges into.</param>
/// <param name="Head">The run branch.</param>
/// <param name="Checks">One line on the CI checks reported on it.</param>
/// <param name="Title">The title sent.</param>
/// <param name="Body">The description sent.</param>
public sealed record DeliveryOutcome(
    DiagnosticReport Report,
    bool Delivered,
    bool Created = false,
    string? Link = null,
    int? Number = null,
    bool? IsDraft = null,
    string? Base = null,
    string? Head = null,
    string? Checks = null,
    string? Title = null,
    string? Body = null)
{
    /// <summary>Whether the refusal is the provider one, which sends the conductor to the delivery agent instead.</summary>
    public bool NeedsDeliveryAgent => Report.Items.Any(item => item.Code == RunDelivery.ProviderUnavailable);
}

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
    public const string ProviderUnavailable = "KW-DELIVER-005";

    /// <summary>The push was refused.</summary>
    public const string PushFailed = "KW-DELIVER-006";

    /// <summary>The pull request could not be created, updated or read back.</summary>
    public const string PullRequestFailed = "KW-DELIVER-007";

    /// <summary>A step done.</summary>
    public const string Step = "KW-DELIVER-008";

    /// <summary>The longest summary accepted.</summary>
    public const int MaxSummaryLength = 1000;

    private const string Remote = "origin";
    private const string ScratchFolder = ".agent_scratchpad";
    private const string TaskGateFile = "task-gate.json";

    private const string DeliveryAgentHint =
        "Dispatch the delivery agent (github-devops) with the create-pull-request skill to open the pull request instead.";

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
    private readonly KyberWeaveConfig _config;
    private readonly IProcessExecutor _executor;
    private readonly string _homeDirectory;
    private readonly ActivePlanFiles _files;

    /// <summary>Creates the deliver step for one checkout.</summary>
    /// <param name="repoRoot">The run's worktree.</param>
    /// <param name="config">The repository's effective configuration.</param>
    /// <param name="executor">Runs git and gh; null for the bounded default.</param>
    /// <param name="homeDirectory">The operator's home, which holds the run records; null for the user profile.</param>
    public RunDelivery(string repoRoot, KyberWeaveConfig config, IProcessExecutor? executor = null, string? homeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
        _executor = executor ?? new BoundedExecutor(TimeSpan.FromMinutes(3));
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _files = new ActivePlanFiles(_repoRoot, config);
    }

    /// <summary>Delivers the run, or refuses and changes nothing.</summary>
    public DeliveryOutcome Run(DeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        DiagnosticReport report = new();
        DeliveryOutcome Refused(string code, string message, string? hint = null)
        {
            report.Add(new Diagnostic(code, Severity.Error, message, "deliver", Hint: hint));
            return new DeliveryOutcome(report, false);
        }

        string summary = request.Summary?.Trim() ?? string.Empty;
        if (summary.Length == 0)
            return Refused(InvalidRequest, "No summary was given. Nothing was changed.", "Pass one to three plain sentences: the problem and the outcome.");
        if (summary.Length > MaxSummaryLength)
            return Refused(InvalidRequest, $"The summary is longer than {MaxSummaryLength} characters. Nothing was changed.", "Keep it to one to three sentences.");

        string branch = Git("symbolic-ref", "--quiet", "--short", "HEAD").StandardOutput.Trim();
        if (branch.Length == 0)
            return Refused(NotARunBranch, "The checkout is not on a branch (detached HEAD). Nothing was changed.", "Call this from the run's worktree, on the run branch.");
        if (!TryParseRunBranch(branch, out string harness, out string type, out string slug))
        {
            return Refused(NotARunBranch,
                $"'{branch}' is not a run branch: run branches are named [<owner>.<agent>.]<harness>/<type>/<slug>. Nothing was changed.",
                "Deliver only a branch that kyber-weave run start created.");
        }

        if (!RunBaseState.TryReadBaseRef(_homeDirectory, branch, out string? baseRef) || baseRef is null)
        {
            return Refused(NotARunBranch, $"No run record names the base '{branch}' was started from. Nothing was changed.",
                "Deliver only a branch kyber-weave run start created or resumed (--resume <branch>), which records its base.");
        }

        string? originHead = Git("symbolic-ref", "--quiet", "--short", $"refs/remotes/{Remote}/HEAD") is { ExitCode: 0 } head
            ? head.StandardOutput.Trim()
            : null;
        string baseBranch = RunStartPlanner.BaseBranchName(baseRef, originHead);
        string? defaultBranch = originHead is null ? null : RunStartPlanner.BaseBranchName(originHead, null);
        if (string.Equals(branch, baseBranch, StringComparison.Ordinal) || string.Equals(branch, defaultBranch, StringComparison.Ordinal)
            || string.Equals(baseBranch, "HEAD", StringComparison.Ordinal))
        {
            return Refused(NotARunBranch, $"'{branch}' is the base or default branch, or its base cannot be named. Nothing was changed.",
                "Deliver only the run's own branch, never the branch it merges into.");
        }

        string remoteUrl = Git("remote", "get-url", Remote).StandardOutput.Trim();
        if (GitHubRepository(remoteUrl) is not { } repository)
        {
            string host = remoteUrl.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) || remoteUrl.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase)
                ? "Azure DevOps"
                : remoteUrl.Length == 0 ? "no origin remote" : "a host other than github.com";
            return Refused(ProviderUnavailable, $"The origin remote is {host}; this step opens pull requests on github.com only. Nothing was changed.", DeliveryAgentHint);
        }

        ProcessResult status = Git("status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status.ExitCode != 0)
            return Refused(UncommittedChanges, $"git status failed: {FirstLine(status.StandardError)}. Nothing was changed.");

        (string plansFolder, string archiveFolder) = PlanFolders();
        string todoFolder = TodoFolder();
        List<string> dirty = [.. ChangedPaths(status.StandardOutput).Where(path => !IsUnder(path, ScratchFolder))];
        List<string> pendingPlanFiles = [.. dirty.Where(path => IsUnder(path, plansFolder) || IsUnder(path, archiveFolder))];
        List<string> pendingTodoFiles = [.. dirty.Where(path => IsUnder(path, todoFolder))];
        List<string> foreign = [.. dirty.Except(pendingPlanFiles, StringComparer.Ordinal).Except(pendingTodoFiles, StringComparer.Ordinal)];
        if (foreign.Count > 0)
        {
            return Refused(UncommittedChanges,
                $"Uncommitted changes outside the plan and todo files: {string.Join(", ", foreign.Take(5))}{(foreign.Count > 5 ? $" and {foreign.Count - 5} more" : string.Empty)}. Nothing was changed.",
                "Each worker commits its own changes. Send these to the worker that owns them, or restore files a tool touched by mistake, then call this again.");
        }

        string mergeBase = Git("merge-base", baseRef, "HEAD").StandardOutput.Trim();
        if (mergeBase.Length == 0)
            return Refused(NotARunBranch, $"'{branch}' shares no history with its base '{baseRef}'. Nothing was changed.");

        if (!TryFindPlan(request.PlanFile, mergeBase, plansFolder, archiveFolder, status.StandardOutput, report, out string? plan))
            return new DeliveryOutcome(report, false);

        PlanFacts? facts = plan is null ? null : RunEvidence.ReadPlan(File.ReadAllText(_files.Absolute(plan)));
        string title = Title(request.Title, facts, harness, type, slug);
        if (!TitleFollowsConvention(title, harness))
        {
            return Refused(InvalidRequest,
                $"The title '{title}' does not follow <type>(<scope>): <summary> [{harness}], with the harness once, at the end. Nothing was changed.",
                "Leave the title out to have it composed, or fix the override.");
        }

        if (Gh("--version") is { ExitCode: not 0 } version)
            return Refused(ProviderUnavailable, $"The GitHub CLI (gh) could not be run: {FirstLine(version.StandardError)}. Nothing was changed.", DeliveryAgentHint);
        if (Gh("auth", "status", "--hostname", "github.com") is { ExitCode: not 0 } auth)
            return Refused(ProviderUnavailable, $"The GitHub CLI is not signed in to github.com: {FirstLine(auth.StandardError + auth.StandardOutput)}. Nothing was changed.", DeliveryAgentHint);

        if (pendingPlanFiles.Count > 0 && !CommitPlanFiles([.. new[] { plansFolder, archiveFolder }.Where(folder => Directory.Exists(_files.Absolute(folder)))], plan, report))
            return new DeliveryOutcome(report, false);
        if (pendingTodoFiles.Count > 0 && !CommitTodoFiles(todoFolder, report))
            return new DeliveryOutcome(report, false);

        PullRequestContent content = Content(request, summary, facts, plan, type, slug, branch, mergeBase) with
        {
            FollowUps = FiledTodos(mergeBase, todoFolder)
        };
        string body = PullRequestDescription.Render(content, PullRequestDescription.ReadHostTemplate(_repoRoot));

        ProcessResult push = Git("push", "--porcelain", Remote, $"refs/heads/{branch}:refs/heads/{branch}");
        if (push.ExitCode != 0)
        {
            return Refused(PushFailed, $"git push of '{branch}' was refused: {FirstLine(push.StandardError + push.StandardOutput)}.",
                "Never force it. If the remote branch has commits this one lacks, a person decides how to reconcile them.");
        }

        Say(report, $"Pushed {branch} to {Remote}.");
        return PullRequest(report, repository, branch, baseBranch, title, body, request.Draft);
    }

    /// <summary>Splits a run branch, <c>[&lt;owner&gt;.&lt;agent&gt;.]&lt;harness&gt;/&lt;type&gt;/&lt;slug&gt;</c>, into its parts.</summary>
    public static bool TryParseRunBranch(string branch, out string harness, out string type, out string slug)
    {
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

    private PullRequestContent Content(DeliveryRequest request, string summary, PlanFacts? facts, string? plan, string type, string slug, string branch, string mergeBase)
    {
        List<string> validation = [];
        List<string> notVerified = [.. (facts?.NotVerified ?? []).Select(item => char.ToUpperInvariant(item[0]) + item[1..])];

        (string? taskLine, IReadOnlyList<string> taskNotVerified) = RunEvidence.SummarizeTaskGates(TaskGateReports(mergeBase));
        if (taskLine is null)
            notVerified.Add("No per-task check results were recorded for this run.");
        else
            validation.Add(taskLine);
        notVerified.AddRange(taskNotVerified);

        ReviewEvidence? review = Review(branch, mergeBase);
        (string reviewLine, IReadOnlyList<string> pending) = RunEvidence.SummarizeReview(review);
        if (review is null)
            notVerified.Add("No code review result was recorded for this branch.");
        else
            validation.Add(reviewLine);

        List<string> decisions = [.. facts?.LowConfidenceDecisions ?? []];
        if (decisions.Count > 0 && facts!.AcceptedRecommendations > 0)
            decisions.Add(facts.AcceptedRecommendations == 1
                ? "One other decision followed the planner's recommendation; the plan lists it."
                : $"{facts.AcceptedRecommendations.ToString(CultureInfo.InvariantCulture)} other decisions followed the planner's recommendation; the plan lists them.");

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

    private DeliveryOutcome PullRequest(DiagnosticReport report, string repository, string branch, string baseBranch, string title, string body, bool draft)
    {
        DeliveryOutcome Failed(string message)
        {
            report.Add(new Diagnostic(PullRequestFailed, Severity.Error, $"{message} Branch '{branch}' is already pushed.", "deliver",
                Hint: "The branch is pushed; calling this again creates or updates the pull request without pushing anything new."));
            return new DeliveryOutcome(report, false, Head: branch, Base: baseBranch, Title: title, Body: body);
        }

        ProcessResult list = Gh("pr", "list", "--repo", repository, "--head", branch, "--state", "open", "--json", "number,url,baseRefName");
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
            if (open.ValueKind != JsonValueKind.Object
                || !open.TryGetProperty("number", out JsonElement numberElement)
                || numberElement.ValueKind != JsonValueKind.Number
                || !numberElement.TryGetInt32(out int openNumber))
            {
                return Failed("gh pr list answered an open pull request with no usable number.");
            }

            string number = openNumber.ToString(CultureInfo.InvariantCulture);
            string openBase = Text(open, "baseRefName") ?? string.Empty;
            if (!string.Equals(openBase, baseBranch, StringComparison.Ordinal))
                return Failed($"Pull request #{number} from '{branch}' is open into '{openBase}', not the run's base '{baseBranch}'. A person decides which base is right; it was not changed.");

            ProcessResult edit = GhWithInput(body, "pr", "edit", number, "--repo", repository, "--title", title, "--body-file", "-");
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
            ProcessResult create = GhWithInput(body, [.. arguments]);
            if (create.ExitCode != 0)
                return Failed($"gh pr create failed: {FirstLine(create.StandardError)}.");
            created = true;
            target = create.StandardOutput.Trim().Split('\n')[^1].Trim();
            Say(report, $"Created {(draft ? "a draft" : "a")} pull request from {branch} into {baseBranch}, assigned to the signed-in user.");
        }

        ProcessResult view = Gh("pr", "view", target, "--repo", repository, "--json", "number,url,isDraft,baseRefName,headRefName,title,body,statusCheckRollup");
        if (view.ExitCode != 0)
            return Failed($"gh pr view failed after the {(created ? "create" : "update")}: {FirstLine(view.StandardError)}.");

        try
        {
            using JsonDocument read = JsonDocument.Parse(view.StandardOutput);
            JsonElement pr = read.RootElement;
            if (pr.ValueKind != JsonValueKind.Object
                || !pr.TryGetProperty("number", out JsonElement numberElement)
                || numberElement.ValueKind != JsonValueKind.Number
                || !numberElement.TryGetInt32(out int prNumber)
                || !pr.TryGetProperty("isDraft", out JsonElement draftElement)
                || draftElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || Text(pr, "url") is not { } url
                || Text(pr, "baseRefName") is not { } readBase
                || Text(pr, "headRefName") is not { } readHead)
            {
                return Failed("gh pr view answered without a usable number, url, draft flag, base or head.");
            }

            string readTitle = Text(pr, "title") ?? string.Empty;
            string readBody = Text(pr, "body") ?? string.Empty;
            if (!string.Equals(readTitle, title, StringComparison.Ordinal) || !string.Equals(Normalized(readBody), Normalized(body), StringComparison.Ordinal))
            {
                report.Add(new Diagnostic(Step, Severity.Warning,
                    "The pull request read back with a different title or description than was sent; check it on the page.", "deliver"));
            }

            return new DeliveryOutcome(report, true, created,
                url,
                prNumber,
                draftElement.GetBoolean(),
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
            string state = (Text(check, "conclusion") is { Length: > 0 } conclusion ? conclusion : Text(check, "state") ?? string.Empty).ToUpperInvariant();
            string progress = (Text(check, "status") ?? "COMPLETED").ToUpperInvariant();
            if (progress != "COMPLETED" || state is "" or "PENDING" or "EXPECTED")
                pending++;
            else if (state is "SUCCESS" or "NEUTRAL" or "SKIPPED")
                passed++;
            else
                failed++;
        }

        return $"{rollup.GetArrayLength().ToString(CultureInfo.InvariantCulture)} CI checks reported: {passed} passed, {failed} failed, {pending} pending";
    }

    /// <summary>A string property of a JSON object; null when the element is not an object or the property is absent or not a string.</summary>
    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private bool TryFindPlan(string? named, string mergeBase, string plansFolder, string archiveFolder, string porcelain, DiagnosticReport report, out string? plan)
    {
        plan = null;
        List<string> added = [.. Git("diff", "--name-only", "--diff-filter=A", "-z", mergeBase, "HEAD", "--")
            .StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        added.AddRange(AddedInWorkingTree(porcelain));

        List<string> active = [.. added.Where(path => IsPlanFile(path, plansFolder) && File.Exists(_files.Absolute(path))).Distinct(StringComparer.Ordinal)];
        if (active.Count > 0)
        {
            report.Add(new Diagnostic(PlanNotDeliverable, Severity.Error,
                $"The plan this branch added is not archived yet: {string.Join(", ", active)}. Nothing was changed.", "deliver",
                Hint: "Archive it first with the archive tool, then call this again."));
            return false;
        }

        List<string> archived = [.. added.Where(path => IsPlanFile(path, archiveFolder) && File.Exists(_files.Absolute(path))).Distinct(StringComparer.Ordinal)];
        if (!string.IsNullOrWhiteSpace(named))
        {
            string wanted = Path.GetFileName(named.Trim().Replace('\\', '/'));
            plan = archived.FirstOrDefault(path => string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(Path.GetFileNameWithoutExtension(path), wanted, StringComparison.OrdinalIgnoreCase));
            if (plan is not null)
                return true;
            report.Add(new Diagnostic(PlanNotDeliverable, Severity.Error,
                $"'{named}' is not a plan this branch archived ({(archived.Count == 0 ? "it archived none" : string.Join(", ", archived))}). Nothing was changed.", "deliver"));
            return false;
        }

        if (archived.Count > 1)
        {
            report.Add(new Diagnostic(PlanNotDeliverable, Severity.Error,
                $"This branch archived several plans ({string.Join(", ", archived)}). Nothing was changed.", "deliver",
                Hint: "Name the run's plan with planFile."));
            return false;
        }

        plan = archived.Count == 1 ? archived[0] : null;
        return true;
    }

    /// <summary>Commits the plan folders, which hold every pending change once the other paths were found clean.</summary>
    private bool CommitPlanFiles(IReadOnlyList<string> paths, string? plan, DiagnosticReport report)
    {
        string name = plan is null ? "plan" : DatePrefix().Replace(Path.GetFileNameWithoutExtension(plan), string.Empty) + " plan";
        string message = $"docs(plan): archive {name}";
        ProcessResult add = Git(["add", "-A", "--", .. paths]);
        ProcessResult commit = add.ExitCode == 0 ? Git(["commit", "-m", message, "--", .. paths]) : add;
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

    /// <summary>
    /// The passing-run gate report of each task, from this run only: written after the run started, or measured against
    /// this change's base. A test-first failing run is written to <c>task-gate-red.json</c> (or, in older runs, a
    /// <c>-red</c> folder) and is never read as a failed check.
    /// </summary>
    private List<GateReport> TaskGateReports(string mergeBase)
    {
        string scratch = Path.Combine(_repoRoot, ScratchFolder);
        if (!Directory.Exists(scratch))
            return [];

        DateTimeOffset? started = RunStartedAt();
        List<GateReport> reports = [];
        foreach (string folder in Directory.EnumerateDirectories(scratch))
        {
            string file = Path.Combine(folder, TaskGateFile);
            if (!File.Exists(file) || Path.GetFileName(folder).EndsWith("-red", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                GateReport report = ReviewJson.ReadGates(File.ReadAllText(file));
                bool thisRun = started is { } at
                    ? File.GetLastWriteTimeUtc(file) >= at.UtcDateTime
                    : string.Equals(report.Base, mergeBase, StringComparison.Ordinal);
                if (thisRun)
                    reports.Add(report);
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable report is evidence the run cannot use; the description then says none was recorded.
            }
        }

        return reports;
    }

    private DateTimeOffset? RunStartedAt()
    {
        string branch = Git("symbolic-ref", "--quiet", "--short", "HEAD").StandardOutput.Trim();
        try
        {
            using JsonDocument record = JsonDocument.Parse(File.ReadAllText(RunBaseState.StatePath(_homeDirectory, branch)));
            return record.RootElement.TryGetProperty("StartedAt", out JsonElement at) && at.ValueKind == JsonValueKind.String
                   && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset value)
                ? value
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The last review cycle that belongs to this change: this branch, this base, a head HEAD descends from.</summary>
    private ReviewEvidence? Review(string branch, string mergeBase)
    {
        string scratch = Path.Combine(_repoRoot, ScratchFolder);
        if (!Directory.Exists(scratch))
            return null;

        string head = Git("rev-parse", "HEAD").StandardOutput.Trim();
        ReviewCycleIdentity current = new(branch, mergeBase, head);
        List<ReviewCycleRecord> belonging = [];
        foreach (string folder in Directory.EnumerateDirectories(scratch))
        {
            IReadOnlyList<ReviewCycleRecord> finished = ReviewCycles.ReadFinished(Path.Combine(folder, "review"));
            belonging.AddRange(ReviewCycles.Belonging(finished, current,
                commit => Git("merge-base", "--is-ancestor", commit, "HEAD").ExitCode == 0).Valid);
        }

        if (belonging.Count == 0)
            return null;

        ReviewCycleRecord last = belonging.OrderBy(cycle => cycle.Number).ThenBy(cycle => File.GetLastWriteTimeUtc(cycle.FindingsPath)).Last();
        try
        {
            FindingsReport findings = ReviewJson.ReadFindings(File.ReadAllText(last.FindingsPath));
            IReadOnlyList<GateResult> gates = last.Gates?.Gates ?? [];
            ReviewOutcome outcome = VerdictEngine.Evaluate(findings.Findings, gates, _config.Review, DateOnly.FromDateTime(DateTime.UtcNow));
            return new ReviewEvidence(last.Number, outcome, gates);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The folder the <c>todo-index</c> registry entry lives in, or empty when the host declares none.</summary>
    private string TodoFolder()
    {
        string? index = _config.ConfigReg.Resolve(_config.Ontology)
            .FirstOrDefault(entry => string.Equals(entry.Name, ConfigRegConfig.TodoIndexProperty, StringComparison.Ordinal))
            ?.Path;
        return index is null ? string.Empty : Path.GetDirectoryName(index)?.Replace('\\', '/') ?? string.Empty;
    }

    /// <summary>
    /// One line per todo this branch added, committed by now: its title and path. These are the follow-up work the run
    /// filed for what it found outside its scope.
    /// </summary>
    private List<string> FiledTodos(string mergeBase, string todoFolder)
    {
        if (todoFolder.Length == 0)
            return [];

        return [.. Git("diff", "--name-only", "--diff-filter=A", "-z", mergeBase, "HEAD", "--")
            .StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => IsPlanFile(path, todoFolder) && File.Exists(_files.Absolute(path)))
            .Order(StringComparer.Ordinal)
            .Select(path => $"{RunEvidence.DocumentTitle(File.ReadAllText(_files.Absolute(path))) ?? Path.GetFileNameWithoutExtension(path)} (`{path}`)")];
    }

    /// <summary>Commits the todo folder, which the follow-up filing wrote after the review and nobody committed.</summary>
    private bool CommitTodoFiles(string todoFolder, DiagnosticReport report)
    {
        const string message = "docs(todo): file the follow-up work this run found";
        ProcessResult add = Git("add", "-A", "--", todoFolder);
        ProcessResult commit = add.ExitCode == 0 ? Git("commit", "-m", message, "--", todoFolder) : add;
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
        string archive = string.Join('/', new[] { root, OntologyConfig.ArchiveSegment, "plans" }.Where(part => part.Length > 0));
        return (plans, archive);
    }

    private static bool IsUnder(string path, string folder) =>
        folder.Length > 0 && path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static bool IsPlanFile(string path, string folder) =>
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

    private ProcessResult Git(params string[] arguments) => Start("git", string.Empty, arguments);

    private ProcessResult Gh(params string[] arguments) => Start("gh", string.Empty, arguments);

    private ProcessResult GhWithInput(string standardInput, params string[] arguments) => Start("gh", standardInput, arguments);

    private ProcessResult Start(string program, string standardInput, IEnumerable<string> arguments)
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
            return _executor.Run(startInfo, standardInput);
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return new ProcessResult(-1, string.Empty, $"{program} could not be run: {exception.Message}");
        }
    }

    /// <summary>The production executor: the repository's deadlock-safe runner, under one time limit per call.</summary>
    private sealed class BoundedExecutor(TimeSpan timeout) : IProcessExecutor
    {
        public ProcessResult Run(ProcessStartInfo startInfo, string standardInput) =>
            ProcessRunner.Run(startInfo, standardInput, timeout);
    }

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
