using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IReviewWorkTreeService"/>: answers the tree, the change and the prior cycles a test declares.</summary>
/// <remarks>
/// The default keeps every path as its own work tree and finds no change, so a caller under test runs its own logic on
/// values the test states rather than on a checkout. A prior cycle's split is delegated to
/// <see cref="ReviewCyclePlanning"/>, which is the same pure rule the adapter uses.
/// </remarks>
internal sealed class FakeReviewWorkTreeService : IReviewWorkTreeService
{
    /// <summary>The work tree each path belongs to, by path; a path not named here is its own work tree.</summary>
    public Dictionary<string, string> Trees { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The identity each work tree reports, by work tree; null when not named.</summary>
    public Dictionary<string, ReviewCycleIdentity> Identities { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The live-run note to report, or null for none.</summary>
    public string? LiveRunNoteText { get; init; }

    /// <summary>The home directory the run records are read from.</summary>
    public string Home { get; init; } = "/kw/home";

    /// <summary>The commits an ancestor-of-head check says yes to.</summary>
    public HashSet<string> Ancestors { get; init; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Containing(string path) =>
        Trees.TryGetValue(path, out string? tree) ? tree : path;

    /// <inheritdoc />
    public ReviewCycleIdentity? IdentityOf(string workTree, string? baseRef) =>
        Identities.TryGetValue(workTree, out ReviewCycleIdentity? identity) ? identity : null;

    /// <inheritdoc />
    public PriorCycles Prior(
        string workTree,
        string? baseRef,
        IReadOnlyList<ReviewCycleRecord> finished,
        string? currentCycleFolder = null)
    {
        ArgumentNullException.ThrowIfNull(finished);
        return ReviewCyclePlanning.Belonging(finished, IdentityOf(workTree, baseRef), Ancestors.Contains);
    }

    /// <inheritdoc />
    public string? LiveRunNote(string workTree, string homeDirectory) => LiveRunNoteText;

    /// <inheritdoc />
    public string HomeDirectory() => Home;
}
