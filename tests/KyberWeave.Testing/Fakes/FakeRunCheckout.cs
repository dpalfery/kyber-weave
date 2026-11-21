using KyberWeave.Contracts.Services;
using KyberWeave.Core.RunStart;

namespace KyberWeave.Tests.Fakes;

/// <summary>An <see cref="IRunCheckout"/> a test declares: which common git directories name a canonical checkout, and which runs are live.</summary>
internal sealed class FakeRunCheckout : IRunCheckout
{
    /// <summary>The canonical checkout each common git directory names; absent means a linked worktree.</summary>
    public Dictionary<string, string> CanonicalCheckouts { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The live separate runs every read returns; null when the run records cannot be read.</summary>
    public IReadOnlyList<RunRecord>? Runs { get; set; } = [];

    /// <inheritdoc />
    public string? CanonicalCheckoutOf(string commonDirectory) =>
        CanonicalCheckouts.TryGetValue(commonDirectory, out string? checkout) ? checkout : null;

    /// <inheritdoc />
    public IReadOnlyList<RunRecord>? LiveSeparateRuns(string homeDirectory, string canonicalCheckout) => Runs;

    /// <inheritdoc />
    public string? CanonicalCheckoutContaining(string path) => null;

    /// <inheritdoc />
    public string? StrayWriteRefusal(string path, string workingDirectory, string homeDirectory) => null;

    /// <inheritdoc />
    public string? ReadCheckedOutBranch(string worktree) => null;
}
