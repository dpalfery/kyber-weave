using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.RunStart;

/// <summary>
///     The run-start decisions, with no process, clock or file system behind them, so the rules can be
///     read and reasoned about without a repository.
/// </summary>
/// <remarks>
///     <para>
///         The branch shape is <c>[&lt;owner&gt;.&lt;agent&gt;.]&lt;harness&gt;/&lt;type&gt;/&lt;slug&gt;</c>, the same
///         shape <c>scripts/check-pr-name.sh</c> holds a pull request to. The two deliberately share one
///         vocabulary: a branch the naming check rejects is a branch whose pull request cannot reach the
///         default branch, so creating one only moves the discovery to the end of the work.
///     </para>
///     <para>
///         The rules that need neither the squad catalogue nor the configuration live in
///         <see cref="RunStartNaming" /> in the domain layer; this type forwards to them and keeps the rest.
///     </para>
/// </remarks>
public static class RunStartPlanner
{
    /// <summary>
    ///     The host a remote URL names, or null when the URL is a local path a run cannot attribute to
    ///     a server.
    /// </summary>
    /// <param name="remoteAddress">The remote's address as git reports it.</param>
    public static string? HostOf(string? remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress))
            return null;

        string text = remoteAddress.Trim();
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

    /// <summary>The harness token for work named after no harness.</summary>
    public const string ManualHarness = RunStartNaming.ManualHarness;

    /// <summary>The remote every run starts from.</summary>
    public const string Remote = RunStartNaming.Remote;

    /// <summary>The remote-tracking ref that names the remote's default branch.</summary>
    public const string RemoteHead = RunStartNaming.RemoteHead;

    /// <summary>
    ///     The skill that answers the onboarding question: it walks the repository's technologies and their
    ///     default gates and standards, so a run never plans against checks nobody chose.
    /// </summary>
    public const string OnboardingSkill = "kyber-docs-onboarding-and-assessment";

    /// <summary>The conventional-commit types a branch may name, in the order the naming check lists them.</summary>
    public static IReadOnlyList<string> ConventionalTypes => RunStartNaming.ConventionalTypes;

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
            refusal = RunStartNaming.TypeRefusal(type);
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

    /// <summary>Whether <paramref name="type" /> is a conventional-commit type.</summary>
    public static bool IsConventionalType(string? type) => RunStartNaming.IsConventionalType(type);

    /// <summary>Whether <paramref name="slug" /> is lowercase, hyphen-separated words.</summary>
    public static bool IsSlug(string? slug) => RunStartNaming.IsSlug(slug);

    /// <summary>Chooses where the new branch is created: in the operator's checkout, or in a worktree of its own.</summary>
    /// <param name="checkoutIsClean">Whether the checkout has no uncommitted change.</param>
    /// <param name="anotherWorktreeIsAttached">Whether the repository already has a second worktree.</param>
    public static RunStartLocation DecideLocation(bool checkoutIsClean, bool anotherWorktreeIsAttached) =>
        RunStartNaming.DecideLocation(checkoutIsClean, anotherWorktreeIsAttached);

    /// <summary>The branch name a run reports its base under, resolved from the ref it actually starts at.</summary>
    /// <param name="baseRef">The remote-tracking ref, as <see cref="BaseRefFor" /> returned it.</param>
    /// <param name="originHeadSymbolicRef">What <c>origin/HEAD</c> points at, or null when git could not say.</param>
    public static string BaseBranchName(string baseRef, string? originHeadSymbolicRef) =>
        RunStartNaming.BaseBranchName(baseRef, originHeadSymbolicRef);

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
    ///     Whether a run must stop before any other check because the onboarding question is unanswered.
    /// </summary>
    /// <param name="config">The config-registry section the run loaded for the repository.</param>
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
    public static bool NeedsOnboarding(ConfigRegConfig config, bool repositoryIsConfigured)
    {
        ArgumentNullException.ThrowIfNull(config);
        return repositoryIsConfigured && string.IsNullOrWhiteSpace(config.Onboarding);
    }
}
