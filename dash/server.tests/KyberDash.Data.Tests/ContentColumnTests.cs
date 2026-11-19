using KyberDash.Data.Repositories;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>
/// Reads the <c>content_json</c> column the way a real store holds it. The values are not always
/// JSON strings: a legacy writer recorded span attributes verbatim, so a boolean or a number sits
/// beside the text. Decoding must keep that row readable instead of failing the whole listing.
/// </summary>
public class ContentColumnTests
{
    // Copied from a claude-code interaction row in a real 272k-record store. That row made every
    // analytics operation throw "The JSON value could not be converted to System.String".
    private const string RealInteractionContentJson =
        """
        {"log_body":"claude_code.api_request_body","service.name":"claude-code-desktop",
         "anthropic.custom_base_url":false,"anthropic.plan":"pro",
         "event.sequence":46,"is_async":true,"model_swapped":false,
         "cache_creation_1h_tokens":0,"cost_usd":0.0125,"total_tool_uses":3}
        """;

    [Fact]
    public void ARecordedAttributeValueThatIsNotTextStillReads()
    {
        Dictionary<string, string> content = CanonStoreRepository.ParseContentColumn(RealInteractionContentJson);

        Assert.Equal("claude_code.api_request_body", content["log_body"]);
        Assert.Equal("false", content["anthropic.custom_base_url"]);
        Assert.Equal("true", content["is_async"]);
        Assert.Equal("46", content["event.sequence"]);
        Assert.Equal("0.0125", content["cost_usd"]);
        Assert.Equal("0", content["cache_creation_1h_tokens"]);
    }

    [Fact]
    public void ANestedValueIsKeptAsItsJsonText()
    {
        Dictionary<string, string> content = CanonStoreRepository.ParseContentColumn(
            """{"tool_parameters":{"bash_command":"git"},"tags":["a","b"]}""");

        Assert.Equal("""{"bash_command":"git"}""", content["tool_parameters"]);
        Assert.Equal("""["a","b"]""", content["tags"]);
    }

    [Fact]
    public void AColumnThatIsNotAJsonObjectReadsAsEmpty()
    {
        Assert.Empty(CanonStoreRepository.ParseContentColumn("[]"));
        Assert.Empty(CanonStoreRepository.ParseContentColumn("null"));
        Assert.Empty(CanonStoreRepository.ParseContentColumn(string.Empty));
    }

    [Fact]
    public void ACorruptColumnReadsAsEmptyInsteadOfFailingTheListing()
    {
        Assert.Empty(CanonStoreRepository.ParseContentColumn("{\"log_body\":"));
    }
}
