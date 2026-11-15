using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class ClaudeHookEventParserTests
{
    [Fact]
    public void ReadsFilePathAndCwd()
    {
        const string json = """
            {"hook_event_name":"PostToolUse","tool_name":"Edit","cwd":"/repo","tool_input":{"file_path":"/repo/src/A.cs","old_string":"x"}}
            """;

        Assert.Equal(new HookEditEvent("/repo/src/A.cs", "/repo"), ClaudeHookEventParser.Parse(json));
    }

    [Fact]
    public void AcceptsAMissingCwd()
    {
        Assert.Equal(new HookEditEvent("a.cs", string.Empty), ClaudeHookEventParser.Parse("""{"tool_input":{"file_path":"a.cs"}}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"tool_input":{}}""")]
    [InlineData("""{"tool_input":{"file_path":3}}""")]
    [InlineData("""{"tool_input":{"file_path":"  "}}""")]
    [InlineData("""{"cwd":"/repo"}""")]
    public void RejectsInputWithoutAFilePath(string json)
    {
        Assert.Null(ClaudeHookEventParser.Parse(json));
    }
}
