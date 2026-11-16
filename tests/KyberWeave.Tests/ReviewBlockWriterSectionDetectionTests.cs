using KyberWeave.Core.Docs.Scaffolding;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <see cref="ReviewBlockWriter.HasReviewSection"/> must see a top-level <c>review:</c> key wherever a
/// YAML parser would: first in a file that starts with a byte-order mark, or after a lone carriage return.
/// A missed key makes <c>docs init</c> append a second <c>review:</c>, which is invalid YAML.
/// </summary>
public sealed class ReviewBlockWriterSectionDetectionTests
{
    [Theory]
    [InlineData("review:\n  gates: []\n")]
    [InlineData("﻿review:\n  gates: []\n")]
    [InlineData("ontology: x\nreview:\n")]
    [InlineData("ontology: x\r\nreview:\r\n")]
    [InlineData("ontology: x\rreview:\r")]
    public void HasReviewSectionFindsTheKey(string config) =>
        Assert.True(ReviewBlockWriter.HasReviewSection(config));

    [Theory]
    [InlineData("# review:\n")]
    [InlineData("ontology: x\n  review: nested\n")]
    [InlineData("xreview:\n")]
    [InlineData("")]
    public void HasReviewSectionIgnoresOtherText(string config) =>
        Assert.False(ReviewBlockWriter.HasReviewSection(config));
}
