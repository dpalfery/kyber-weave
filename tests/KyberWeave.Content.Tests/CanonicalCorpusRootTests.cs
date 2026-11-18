using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>The shipped corpus exposes its root and the Kyber Squad product folder through the corpus seam.</summary>
public sealed class CanonicalCorpusRootTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void ToolRoot_ExistsForCanonicalCorpus()
    {
        Assert.True(
            Corpus.DirectoryExists(string.Empty),
            $"Expected the corpus root at '{Corpus.Root}'.");
        Assert.True(
            Corpus.DirectoryExists("products/kyber-squad"),
            $"Expected products/kyber-squad under the corpus root at '{Corpus.Root}'.");
    }
}
