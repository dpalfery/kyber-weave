using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Domain.Tests.Hooks;

public sealed class ClaudeHookEventParserTests
{
    [Fact]
    public void ReadsFilePathCwdAndToolName()
    {
        const string json = """
            {"hook_event_name":"PostToolUse","tool_name":"Edit","cwd":"/repo","tool_input":{"file_path":"/repo/src/A.cs","old_string":"x"}}
            """;

        Assert.True(ClaudeHookEventParser.TryParse(json, out HookEditEvent? edit));
        Assert.Equal(new HookEditEvent("/repo/src/A.cs", "/repo", "Edit"), edit);
    }

    [Fact]
    public void AcceptsAMissingCwd()
    {
        Assert.True(ClaudeHookEventParser.TryParse("""{"tool_input":{"file_path":"a.cs"}}""", out HookEditEvent? edit));
        Assert.Equal(string.Empty, edit!.Cwd);
        Assert.Null(edit.ToolName);
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
        Assert.False(ClaudeHookEventParser.TryParse(json, out HookEditEvent? edit));
        Assert.Null(edit);
    }
}
