using System.Text.Json;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Review documents are the seam between an agentic layer and a deterministic one. A
/// missing required constructor parameter must fail the parse rather than silently
/// default — that is the whole of <c>RespectRequiredConstructorParameters</c>.
/// </summary>
public sealed class ReviewJsonTests
{
    [Fact]
    public void AnEmptyFindingsObjectIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadFindings("{}"));
    }

    [Fact]
    public void AnEmptyGatesObjectIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadGates("{}"));
    }

    [Fact]
    public void AnEmptyDuplicatesObjectIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadDuplicates("{}"));
    }

    [Fact]
    public void AFindingsReportMissingRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadFindings(
            """{"schema":"kyber-weave.review-findings/v1","findings":[],"changedPaths":[]}"""));
    }

    [Fact]
    public void AGateReportWhoseGateOmitsRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadGates(
            """{"schema":"kyber-weave.review-gates/v1","gates":[{}]}"""));
    }

    [Fact]
    public void ADuplicatesReportMissingRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadDuplicates(
            """{"schema":"kyber-weave.review-duplicates/v1","clusters":[]}"""));
    }

    [Fact]
    public void AFindingThatOmitsRequiredConstructorParametersIsRejected()
    {
        Assert.Throws<JsonException>(() => ReviewJson.ReadFindings(
            """
            {
              "schema": "kyber-weave.review-findings/v1",
              "findings": [{}],
              "changedPaths": ["src/Foo.cs"],
              "changedLines": 1
            }
            """));
    }
}
