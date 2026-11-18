using KyberWeave.Tests.Configuration;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>This repository's own host config, as the config reader loads it from the checkout.</summary>
public sealed class ArbiterRepositoryHostConfigTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void ThisRepositoryHostConfig_LoadsWithProviderKindNone()
    {
        KyberWeaveConfig config = ConfigFileReader.Load(Corpus.Root);

        Assert.Equal(ArbiterProviderKind.None, config.Arbiter.Provider.Kind);
    }
}
