using KyberDash.Data.Collector;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Domain.Tests;

/// <summary>
/// Wire-name coverage for <see cref="ContentBuckets.TryMap"/>: every content bucket the OTLP
/// span mappers, the OTLP log mapper, and the canon ingest pipeline accept must resolve through
/// the single shared helper, and anything else must be rejected.
/// </summary>
public class ContentBucketsTests
{
    [Theory]
    [InlineData("system_prompt", ContentBucket.SystemPrompt)]
    [InlineData("tool_definitions", ContentBucket.ToolDefinitions)]
    [InlineData("tool_definition", ContentBucket.ToolDefinition)]
    [InlineData("instruction_context", ContentBucket.InstructionContext)]
    [InlineData("conversation_history", ContentBucket.ConversationHistory)]
    [InlineData("tool_result_content", ContentBucket.ToolResultContent)]
    [InlineData("user_prompt", ContentBucket.UserPrompt)]
    [InlineData("user_message", ContentBucket.UserMessage)]
    [InlineData("reasoning", ContentBucket.Reasoning)]
    public void TryMap_MapsEveryKnownWireBucket(string wire, ContentBucket expected)
    {
        Assert.True(ContentBuckets.TryMap(wire, out ContentBucket actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SYSTEM_PROMPT")]
    [InlineData("System_Prompt")]
    [InlineData("unknown_bucket")]
    [InlineData("tool-result-content")]
    public void TryMap_RejectsUnknownBuckets(string wire)
    {
        Assert.False(ContentBuckets.TryMap(wire, out ContentBucket actual));
        Assert.Equal(default, actual);
    }
}
