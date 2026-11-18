using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>A <c>hooks</c> value of the wrong shape is the user's content, so it is never reported empty.</summary>
public sealed class SquadHookJsonBlockMalformedHooksTests
{
    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor, "{\"hooks\":\"oops\"}")]
    [InlineData(SquadHookBlockFormat.Codex, "{\"hooks\":[]}")]
    [InlineData(SquadHookBlockFormat.Claude, "{\"hooks\":42}")]
    public void IsEmptyIsFalseWhenHooksIsNotAnObject(SquadHookBlockFormat format, string json) =>
        Assert.False(SquadHookJsonBlock.IsEmpty(format, json));

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor, "{}")]
    [InlineData(SquadHookBlockFormat.Codex, "{\"hooks\":{}}")]
    [InlineData(SquadHookBlockFormat.Claude, "{\"hooks\":null}")]
    public void IsEmptyIsTrueWhenHooksIsAbsentNullOrEmpty(SquadHookBlockFormat format, string json) =>
        Assert.True(SquadHookJsonBlock.IsEmpty(format, json));
}
