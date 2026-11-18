using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Keeps the retired <c>review verdict</c> command form out of every product file and the root
/// <c>AGENTS.md</c>. The review council loop's rules are checked by the prose rule catalog,
/// <c>Fixtures/prose-rules/catalog.yml</c>.
/// </summary>
public sealed class ReviewCouncilLoopContractTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    /// <summary>
    /// Every file under the product root, and the root <c>AGENTS.md</c>.
    /// </summary>
    private static IEnumerable<string> ScannedFiles()
    {
        foreach (string file in Corpus.EnumerateFiles("products/kyber-squad", "*"))
        {
            yield return file;
        }

        yield return "AGENTS.md";
    }

    [Theory]
    [InlineData("kyber-weave review verdict . --findings")]
    public void RetiredPhraseIsAbsentFromEveryProductFileAndTheRootAgentsFile(string retiredPhrase)
    {
        List<string> files = ScannedFiles().ToList();
        Assert.NotEmpty(files);

        string[] offenders = files
            .Where(file => Corpus.ReadText(file).Contains(retiredPhrase, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Retired phrase '{retiredPhrase}' is still present in: {string.Join(", ", offenders)}");
    }
}
