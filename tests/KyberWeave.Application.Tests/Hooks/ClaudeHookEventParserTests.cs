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

        HookParseResult result = ClaudeHookEventParser.Parse(json);

        Assert.Equal(new HookEditEvent("/repo/src/A.cs", "/repo"), result.Edit);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void AcceptsAMissingCwd()
    {
        Assert.Equal(new HookEditEvent("a.cs", string.Empty), ClaudeHookEventParser.Parse("""{"tool_input":{"file_path":"a.cs"}}""").Edit);
    }

    [Theory]
    [InlineData("""{"tool_input":{}}""")]
    [InlineData("""{"tool_input":{"file_path":null}}""")]
    [InlineData("""{"tool_input":{"file_path":"  "}}""")]
    public void TreatsInputWithoutAFilePathAsNotAnEdit(string json)
    {
        HookParseResult result = ClaudeHookEventParser.Parse(json);

        Assert.Null(result.Edit);
        Assert.Null(result.Problem);
    }

    [Theory]
    [InlineData("", "no input")]
    [InlineData(null, "no input")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "not a JSON object")]
    [InlineData("""{"cwd":"/repo"}""", "no 'tool_input'")]
    [InlineData("""{"tool_input":"x"}""", "no 'tool_input'")]
    [InlineData("""{"tool_input":{"file_path":3}}""", "file_path")]
    [InlineData("""{"tool_input":{"file_path":"a.cs"},"cwd":123}""", "'cwd'")]
    public void ReportsAProblemForUnusableInput(string? json, string expected)
    {
        HookParseResult result = ClaudeHookEventParser.Parse(json);

        Assert.Null(result.Edit);
        Assert.Contains(expected, result.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsOversizedInputWithoutParsingIt()
    {
        string json = "{\"tool_input\":{\"file_path\":\"" + new string('a', ClaudeHookEventParser.MaxInputChars) + "\"}}";

        HookParseResult result = ClaudeHookEventParser.Parse(json);

        Assert.Null(result.Edit);
        Assert.Contains("larger than", result.Problem, StringComparison.Ordinal);
    }
}
