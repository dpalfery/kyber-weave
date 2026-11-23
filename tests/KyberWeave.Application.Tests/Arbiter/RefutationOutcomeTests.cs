using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>The refuter's first line read back: each outcome it may state, and the stored word for it.</summary>
public sealed class RefutationOutcomeTests
{
    [Fact]
    public void AnUnverifiedFirstLineParsesAsUnverified() =>
        Assert.Equal(
            RefutationVerdict.Unverified,
            RefutationOutcome.Parse("UNVERIFIED: security/key-in-argv — the scenario depends on runtime behaviour the code does not show."));

    [Fact]
    public void ARefutedFirstLineStillParsesAsRefuted() =>
        Assert.Equal(RefutationVerdict.Refuted, RefutationOutcome.Parse("REFUTED: security/key-in-argv — the value is read from the environment."));

    [Fact]
    public void ASupportedFirstLineStillParsesAsConfirmed() =>
        Assert.Equal(RefutationVerdict.Confirmed, RefutationOutcome.Parse("SUPPORTED: security/key-in-argv — the argv is logged at line 4."));

    [Fact]
    public void AnUnrecognisedFirstLineStillParsesAsUnknown() =>
        Assert.Equal(RefutationVerdict.Unknown, RefutationOutcome.Parse("Looks wrong to me."));

    [Fact]
    public void AnUnverifiedVerdictIsStoredAsTheWordUnverified() =>
        Assert.Equal("unverified", RefutationOutcome.Word(RefutationVerdict.Unverified));
}
