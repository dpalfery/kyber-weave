using System.Text.RegularExpressions;
using KyberWeave.Core.Text;

namespace KyberWeave.Core.RunStart;

/// <summary>The run-start naming and placement rules that need no catalogue, configuration, process or file system.</summary>
/// <remarks><c>RunStartPlanner</c> keeps the rules that read the squad catalogue and the configuration, and forwards the rest here.</remarks>
public static partial class RunStartNaming
{
    /// <summary>The harness token for work named after no harness.</summary>
    public const string ManualHarness = "manual";

    /// <summary>The remote every run starts from.</summary>
    public const string Remote = "origin";

    /// <summary>The remote-tracking ref that names the remote's default branch.</summary>
    public const string RemoteHead = Remote + "/HEAD";

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
    ///     The refusal for a type that is not a conventional-commit type: the valid types, the closest one when
    ///     the value looks like a typo of it, and the defaults, because a run is named for what it commits.
    /// </summary>
    public static string TypeRefusal(string? type)
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
    ///     The branch name a run reports its base under, resolved from the ref it actually starts at.
    /// </summary>
    /// <param name="baseRef">The remote-tracking ref, as <c>BaseRefFor</c> returned it.</param>
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

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
