using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// A Cursor hook entry is not mistaken for a Claude one, so a block owned for one harness never claims the other's
/// entries. Regression test for Kilo review comment 15 on PR #527.
/// </summary>
public sealed class SquadHookJsonBlockSignatureTests
{
    [Fact]
    public void Item15_SquadHookJsonBlock_Cursor_DoesNotMatchClaudeSignature()
    {
        JsonObject claudeEntry = new()
        {
            ["command"] = "kyber-weave-arbiter hook --harness claude",
            ["matcher"] = "Task",
            ["timeout"] = 5,
        };
        JsonObject cursorEntry = new()
        {
            ["command"] = "kyber-weave-arbiter hook --harness cursor",
            ["matcher"] = "Task",
            ["timeout"] = 5,
        };

        Assert.False(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Cursor, claudeEntry));
        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Cursor, cursorEntry));

        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, claudeEntry));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, cursorEntry));
    }
}
