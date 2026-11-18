using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Review;

/// <summary>
/// The shared fake for <see cref="INotSeatedStore"/> answers the way the file-backed store does: a replace takes the
/// decisions as the answer for the owned lenses and keeps every lens the caller does not own.
/// </summary>
public sealed class FakeNotSeatedStoreTests
{
    [Fact]
    public void AReplaceKeepsADecisionForALensTheCallerDoesNotOwn()
    {
        FakeNotSeatedStore store = new();
        store.Add("/cycle", new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" });

        store.Replace("/cycle", ["a"], new Dictionary<string, string>(StringComparer.Ordinal));

        IReadOnlyDictionary<string, string> kept = store.Read("/cycle");
        Assert.Equal(["b"], kept.Keys);
        Assert.Equal("2", kept["b"]);
    }

    [Fact]
    public void AReplaceDropsAnOwnedLensTheCallerNoLongerDecides()
    {
        FakeNotSeatedStore store = new();
        store.Add("/cycle", new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "1", ["b"] = "2" });

        store.Replace("/cycle", ["a"], new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "changed" });

        IReadOnlyDictionary<string, string> kept = store.Read("/cycle");
        Assert.Equal("changed", kept["a"]);
        Assert.Equal("2", kept["b"]);
    }
}
