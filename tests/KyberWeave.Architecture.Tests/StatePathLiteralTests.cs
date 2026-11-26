using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the state-path literals to their single sources: <c>ArbiterStateDirectory</c> for the project store and
/// <c>UserStatePaths</c> for the user-level layout.
/// </summary>
public sealed class StatePathLiteralTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly HashSet<string> SingleSources = new(StringComparer.Ordinal)
    {
        "src/KyberWeave.Kernel/Arbiter/ArbiterStateDirectory.cs",
        "src/KyberWeave.Kernel/FileSystem/UserStatePaths.cs"
    };

    [Fact]
    public void StatePathLiteralsLiveInTheirSingleSource()
    {
        List<string> offenders = [];

        foreach (string path in Corpus.EnumerateFiles("src", "*.cs"))
        {
            if (SingleSources.Contains(path))
            {
                continue;
            }

            string text = Corpus.ReadText(path);
            if (text.Contains("\"artifacts\", \"arbiter\"", StringComparison.Ordinal)
                || text.Contains("\"artifacts\",\"arbiter\"", StringComparison.Ordinal)
                || text.Contains("\".kyber-weave\"", StringComparison.Ordinal))
            {
                offenders.Add(path);
            }
        }

        Assert.Empty(offenders);
    }
}
