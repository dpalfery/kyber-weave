using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Review;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IReviewWorkTreeService"/>: answers the tree, the change and the prior cycles a test declares.</summary>
/// <remarks>
/// A path the test names a tree for answers that tree; every other path answers its full spelling, resolved by the
/// path seam against <see cref="WorkingDirectory"/>, as the service's own <c>Containing</c> would. A prior cycle's
/// split is delegated to <see cref="ReviewCyclePlanning"/>, with the same recorded-identity override the service
/// applies. Nothing here reads a checkout.
/// </remarks>
internal sealed class FakeReviewWorkTreeService : IReviewWorkTreeService
{
    private readonly IPathResolver _paths = FakePathResolver.OverEmptyFileSystem();

    /// <summary>The work tree each path belongs to, by path as given or full; a path not named here is its own work tree.</summary>
    public Dictionary<string, string> Trees { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The identity each work tree reports, by work tree; null when not named.</summary>
    public Dictionary<string, ReviewCycleIdentity> Identities { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The identity each cycle folder recorded, by cycle folder, which a split prefers when no base is named.</summary>
    public Dictionary<string, ReviewCycleIdentity> RecordedIdentities { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The directory a relative path resolves against.</summary>
    public string WorkingDirectory { get; init; } = "/kw/work";

    /// <summary>The live-run note to report, or null for none.</summary>
    public string? LiveRunNoteText { get; init; }

    /// <summary>The home directory the run records are read from.</summary>
    public string Home { get; init; } = "/kw/home";

    /// <summary>The commits an ancestor-of-head check says yes to.</summary>
    public HashSet<string> Ancestors { get; init; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Containing(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Trees.TryGetValue(path, out string? named))
            return named;

        string full = _paths.GetFullPath(path, WorkingDirectory);
        return Trees.TryGetValue(full, out string? tree) ? tree : full;
    }

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
        ReviewCycleIdentity? current = IdentityOf(workTree, baseRef);
        if (current is not null
            && string.IsNullOrWhiteSpace(baseRef)
            && currentCycleFolder is not null
            && RecordedIdentities.TryGetValue(currentCycleFolder, out ReviewCycleIdentity? own)
            && string.Equals(own.Branch, current.Branch, StringComparison.Ordinal))
        {
            current = current with { Base = own.Base };
        }

        return ReviewCyclePlanning.Belonging(finished, current, Ancestors.Contains);
    }

    /// <inheritdoc />
    public string? LiveRunNote(string workTree, string homeDirectory) => LiveRunNoteText;

    /// <inheritdoc />
    public string HomeDirectory() => Home;
}
