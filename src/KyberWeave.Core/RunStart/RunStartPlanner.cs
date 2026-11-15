using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.RunStart;

/// <summary>Where a run creates the branch its work belongs on.</summary>
public enum RunStartLocation
{
    /// <summary>The checkout the run was started in, which is clean and which no other worktree shares.</summary>
    InPlace,

    /// <summary>A worktree of its own, because that checkout is dirty or another worktree shares it.</summary>
    InWorktree
}

/// <summary>
///     The decisions a <c>kyber-weave run start</c> reaches before it touches git: the branch to name, the
///     base to start it from, and whether it may take the operator's checkout.
/// </summary>
public sealed record RunStartPlan(
    string Branch,
    string BaseBranch,
    string BaseRef,
    bool Resumed,
    RunStartLocation Location);

/// <summary>
///     The run-start decisions, with no process, clock or file system behind them, so the rules can be
///     read and reasoned about without a repository.
/// </summary>
/// <remarks>
///     The branch shape is <c>[&lt;owner&gt;.&lt;agent&gt;.]&lt;harness&gt;/&lt;type&gt;/&lt;slug&gt;</c>, the same
///     shape <c>scripts/check-pr-name.sh</c> holds a pull request to. The two deliberately share one
///     vocabulary: a branch the naming check rejects is a branch whose pull request cannot reach the
///     default branch, so creating one only moves the discovery to the end of the work.
/// </remarks>
public static partial class RunStartPlanner
{
    /// <summary>The harness token for work named after no harness.</summary>
    public const string ManualHarness = "manual";

    /// <summary>The remote every run starts from.</summary>
    public const string Remote = "origin";

    /// <summary>The remote-tracking ref that names the remote's default branch.</summary>
    public const string RemoteHead = Remote + "/HEAD";

    /// <summary>
    ///     The skill that answers the onboarding question: it walks the repository's technologies and their
    ///     default gates and standards, so a run never plans against checks nobody chose.
    /// </summary>
    public const string OnboardingSkill = "kyber-docs-onboarding-and-assessment";

    /// <summary>
    ///     The conventional-commit types a branch may name, in the order the naming check lists them.
    /// </summary>
    public static IReadOnlyList<string> ConventionalTypes { get; } =
    [
        "build",
        "chore",
        "ci",
        "docs",
        "feat",
        "fix",
        "perf",
        "refactor",
        "revert",
        "style",
        "test"
    ];

    /// <summary>
    ///     Every harness token a run may be started for: the thirteen catalogue tokens followed by
    ///     <see cref="ManualHarness" />. Taken from the catalogue rather than restated, so adding a harness
    ///     does not leave this command behind.
    /// </summary>
    public static IReadOnlyList<string> HarnessTokens { get; } =
        [.. SquadTargetCatalog.All.Select(SquadTargetCatalog.GetToken), ManualHarness];

    /// <summary>
    ///     Resolves the harness token the operator wrote into the canonical one the branch carries.
    /// </summary>
    /// <param name="token">The token as written, in any case, or one of the catalogue's aliases.</param>
    /// <param name="harness">The canonical token, or the empty string when the token is unknown.</param>
    /// <returns>Whether the token names a harness or <see cref="ManualHarness" />.</returns>
    public static bool TryResolveHarness(string? token, out string harness)
    {
        harness = string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string trimmed = token.Trim();
        if (SquadTargetCatalog.TryResolve(trimmed, out SquadTarget target))
        {
            harness = SquadTargetCatalog.GetToken(target);
            return true;
        }

        if (!string.Equals(trimmed, ManualHarness, StringComparison.Ordinal))
            return false;

        harness = ManualHarness;
        return true;
    }

    /// <summary>
    ///     The refusal for a type that is not a conventional-commit type: the valid types, the closest one when
    ///     the value looks like a typo of it, and the defaults, because a run is named for what it commits.
    /// </summary>
    private static string TypeRefusal(string? type)
    {
        string given = type?.Trim() ?? string.Empty;
        string? closest = given.Length == 0
            ? null
            : ConventionalTypes
                .Select(candidate => (candidate, distance: StringDistance.Levenshtein(given, candidate, ignoreCase: true)))
                .Where(pair => pair.distance <= 2
                               || given.StartsWith(pair.candidate, StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.distance)
                .Select(pair => pair.candidate)
                .FirstOrDefault();
        string suggestion = closest is null ? string.Empty : $" Did you mean '{closest}'?";
        return $"Type '{type}' is not a conventional-commit type. Use one of: {string.Join(", ", ConventionalTypes)}." +
               $"{suggestion} A feature is 'feat' and a defect is 'fix'.";
    }

    /// <summary>Whether <paramref name="type" /> is a conventional-commit type.</summary>
    public static bool IsConventionalType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return false;

        string trimmed = type.Trim();
        return ConventionalTypes.Contains(trimmed, StringComparer.Ordinal);
    }

    /// <summary>Whether <paramref name="slug" /> is lowercase, hyphen-separated words.</summary>
    public static bool IsSlug(string? slug)
    {
        return !string.IsNullOrWhiteSpace(slug) && SlugPattern().IsMatch(slug.Trim());
    }

    /// <summary>
    ///     Composes the branch name the three arguments name, or reports why it cannot.
    /// </summary>
    /// <param name="harness">The harness token as the operator wrote it.</param>
    /// <param name="type">The conventional-commit type.</param>
    /// <param name="slug">The lowercase hyphen-separated slug.</param>
    /// <param name="branch">The composed branch name, or the empty string on a refusal.</param>
    /// <param name="refusal">A message naming what to pass instead, or the empty string on success.</param>
    /// <returns>Whether the three arguments name a branch.</returns>
    public static bool TryNameBranch(
        string? harness,
        string? type,
        string? slug,
        out string branch,
        out string refusal)
    {
        branch = string.Empty;
        refusal = string.Empty;

        if (!TryResolveHarness(harness, out string canonical))
        {
            refusal =
                $"Harness token '{harness}' is not one a run can be started for. Use one of: " +
                $"{string.Join(", ", HarnessTokens)}.";
            return false;
        }

        if (!IsConventionalType(type))
        {
            refusal = TypeRefusal(type);
            return false;
        }

        if (!IsSlug(slug))
        {
            refusal =
                $"Slug '{slug}' is not lowercase hyphen-separated words. Use letters and digits " +
                "separated by single hyphens — 'arbiter-global-block-identity', not 'Arbiter Global Block'.";
            return false;
        }

        branch = $"{canonical}/{type!.Trim()}/{slug!.Trim()}";
        return true;
    }

    /// <summary>
    ///     Chooses where the new branch is created: in the operator's checkout, or in a worktree of its own.
    /// </summary>
    /// <param name="checkoutIsClean">Whether the checkout has no uncommitted change.</param>
    /// <param name="anotherWorktreeIsAttached">Whether the repository already has a second worktree.</param>
    /// <remarks>
    ///     Switching branches under uncommitted work either fails or carries the edits onto a branch they
    ///     were not made for, so a dirty checkout keeps its edits and the run gets a worktree beside it.
    ///     A second worktree attached to the same repository means another worker is standing in this
    ///     repository: taking the checkout would move the branch under them, so that case is treated the
    ///     same way. Creation is also why the caller reports the path rather than assuming the operator's
    ///     directory.
    /// </remarks>
    public static RunStartLocation DecideLocation(bool checkoutIsClean, bool anotherWorktreeIsAttached)
    {
        return checkoutIsClean && !anotherWorktreeIsAttached
            ? RunStartLocation.InPlace
            : RunStartLocation.InWorktree;
    }

    /// <summary>
    ///     The remote-tracking ref a run starts from: the configured base, qualified by the remote.
    /// </summary>
    /// <param name="configuredBaseBranch">
    ///     The <c>run.base-branch</c> setting as the operator wrote it, or null when they set none.
    /// </param>
    /// <remarks>
    ///     <c>main</c> is never assumed. A repository whose default branch is <c>develop</c>, or whose
    ///     operator named a release branch, is compared against the branch that was actually named.
    /// </remarks>
    public static string BaseRefFor(string? configuredBaseBranch)
    {
        string configured = string.IsNullOrWhiteSpace(configuredBaseBranch)
            ? ConfigRegConfig.DefaultBaseBranch
            : configuredBaseBranch.Trim();

        return configured.StartsWith(Remote + "/", StringComparison.Ordinal)
            ? configured
            : Remote + "/" + configured;
    }

    /// <summary>
    ///     The branch name a run reports its base under, resolved from the ref it actually starts at.
    /// </summary>
    /// <param name="baseRef">The remote-tracking ref, as <see cref="BaseRefFor" /> returned it.</param>
    /// <param name="originHeadSymbolicRef">
    ///     What <c>origin/HEAD</c> points at (for example <c>origin/main</c>), or null when git could not
    ///     say. Only read when <paramref name="baseRef" /> is <c>origin/HEAD</c>, which names no branch on
    ///     its own.
    /// </param>
    public static string BaseBranchName(string baseRef, string? originHeadSymbolicRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseRef);

        string resolved = baseRef;
        if (string.Equals(baseRef, RemoteHead, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(originHeadSymbolicRef))
        {
            resolved = originHeadSymbolicRef.Trim();
        }

        return resolved.StartsWith(Remote + "/", StringComparison.Ordinal)
            ? resolved[(Remote.Length + 1)..]
            : resolved;
    }

    /// <summary>
    ///     Whether a run must stop before any other check because the onboarding question is unanswered.
    /// </summary>
    /// <param name="config">The configuration the run loaded for the repository.</param>
    /// <param name="repositoryIsConfigured">
    ///     Whether the repository has a host configuration file at all, which is what makes the question
    ///     meaningful: an operator who never ran <c>docs init</c> has not been offered it.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         An unresolved setting stops the run rather than warning, because the gates and standards the
    ///         run will be held to are exactly what onboarding chooses: proceeding would run work against
    ///         defaults nobody reviewed, and the report of that would arrive at the end of the run instead of
    ///         before it.
    ///     </para>
    ///     <para>
    ///         Absence is the only state that stops. Both recorded answers — <c>done</c> and <c>declined</c> —
    ///         end the question, so a run in a repository that answered never sees this twice.
    ///     </para>
    ///     <para>
    ///         Autonomous mode changes nothing here. The run stops, asks nothing, and leaves the setting as it
    ///         found it: writing an answer on the user's behalf would record a decision nobody made.
    ///     </para>
    ///     <para>
    ///         A repository with no host configuration is not stopped: nobody there has opted into configuring
    ///         this repository, so there is no gates-and-standards choice for onboarding to record yet. That is
    ///         the shape the conductor uses when it checks the project's tooling only where the repository has
    ///         opted in — the check speaks where the repository has, and stays quiet where it has not.
    ///     </para>
    /// </remarks>
    public static bool NeedsOnboarding(KyberWeaveConfig config, bool repositoryIsConfigured)
    {
        ArgumentNullException.ThrowIfNull(config);
        return repositoryIsConfigured && string.IsNullOrWhiteSpace(config.ConfigReg.Onboarding);
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
