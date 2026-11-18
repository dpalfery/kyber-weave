using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IReviewCycleSelector"/>: answers the task, the change and the re-check a test declares.</summary>
/// <remarks>
/// The default chooses the requested task, finds no changed path and re-checks no finding, so a caller test runs the
/// decision it is written for rather than a git checkout. Every call is recorded, so a test can assert what a caller
/// asked for without a file system.
/// </remarks>
internal sealed class FakeReviewCycleSelector : IReviewCycleSelector
{
    /// <summary>The task each review folder reports, by <c>tree|scratchpad|given</c>; otherwise the given task or a default.</summary>
    public Dictionary<string, ReviewTaskChoice> Choices { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The paths each tree changed since the commit named by the key, by <c>tree|commit</c>; null when not named.</summary>
    public Dictionary<string, IReadOnlyList<string>?> Changed { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The findings a re-check returns.</summary>
    public IReadOnlyList<ReviewFinding> Recheck { get; init; } = [];

    /// <summary>Whether the re-check reports a successfully read prior cycle.</summary>
    public bool RecheckOk { get; init; } = true;

    /// <summary>Every change question asked, as the tree and the commit.</summary>
    public List<(string Tree, string Commit)> ChangeQuestions { get; } = [];

    /// <inheritdoc />
    public ReviewTaskChoice ChooseTask(string tree, string scratchpad, string? given) =>
        Choices.TryGetValue($"{tree}|{scratchpad}|{given}", out ReviewTaskChoice? choice)
            ? choice
            : new ReviewTaskChoice(given ?? "task", null, null);

    /// <inheritdoc />
    public IReadOnlyList<string>? ChangedSince(string tree, string commit)
    {
        ChangeQuestions.Add((tree, commit));
        return Changed.TryGetValue($"{tree}|{commit}", out IReadOnlyList<string>? paths) ? paths : null;
    }

    /// <inheritdoc />
    public (bool Ok, IReadOnlyList<ReviewFinding> Recheck) ReadRecheck(
        ReviewCycleRecord prior,
        ReviewPolicy policy,
        DiagnosticReport report,
        string unreadableCode,
        Severity unreadableSeverity,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(report);
        return (RecheckOk, Recheck);
    }
}
