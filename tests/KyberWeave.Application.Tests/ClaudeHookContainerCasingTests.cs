using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the container casing of Squad's Arbiter hooks in a Claude <c>settings.json</c>. Claude Code
/// documents only <c>PreToolUse</c> and <c>PostToolUse</c>, so a lower-case <c>preToolUse</c> or
/// <c>postToolUse</c> container is a spelling the harness never reads: the Arbiter would silently
/// stop gating Agent and Task dispatches. Legacy files that carry the lower-case spelling must be
/// migrated on splice and removal, and the Cursor format keeps its lower-case containers unchanged.
/// </summary>
public sealed class ClaudeHookContainerCasingTests
{
    private const string SquadGroupJson =
        """{"matcher":"^(Agent|Task)$","hooks":[{"type":"command","command":"kyber-weave-arbiter hook --harness claude --scope global"}]}""";

    private const string UserGroupJson =
        """{"matcher":"Bash","hooks":[{"type":"command","command":"user-lint"}]}""";

    private const string CursorSquadPreJson =
        """{"command":"kyber-weave-arbiter hook --harness cursor","matcher":"Task","timeout":5,"failClosed":true}""";

    private const string CursorSquadPostJson =
        """{"command":"kyber-weave-arbiter hook --harness cursor","matcher":"Edit","timeout":10,"failClosed":false}""";

    private const string CursorUserJson =
        """{"command":"user-linter --fast","matcher":"Edit","timeout":3,"failClosed":false}""";

    private static JsonNode Node(string json) => JsonNode.Parse(json)!;

    private static JsonObject Hooks(string json) => JsonNode.Parse(json)!["hooks"]!.AsObject();

    private static string Digest(string json) => SquadHookJsonBlock.CanonicalDigest(Node(json));

    private static int CountSquadEntries(string json)
    {
        int count = 0;
        foreach (KeyValuePair<string, JsonNode?> container in Hooks(json))
        {
            if (container.Value is JsonArray entries)
            {
                count += entries.Count(entry => SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, entry));
            }
        }

        return count;
    }

    /// <summary>
    /// A fresh <c>settings.json</c> gets the documented PascalCase containers, and the owned
    /// pointers name those containers. The lower-case spelling is what a file written before the
    /// fix carries, so writing it here is the regression.
    /// </summary>
    [Fact]
    public void SpliceContent_ClaudeNullSettings_WritesPascalCaseContainersAndOwnedPointers()
    {
        (string content, IReadOnlyList<SquadHookOwnedEntry> owned) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Claude,
            null,
            [Node(SquadGroupJson)],
            [Node(SquadGroupJson)]);

        JsonObject hooks = Hooks(content);
        Assert.True(hooks.ContainsKey("PreToolUse"), $"Claude settings must hold 'PreToolUse'. Written: {content}");
        Assert.True(hooks.ContainsKey("PostToolUse"), $"Claude settings must hold 'PostToolUse'. Written: {content}");
        Assert.False(hooks.ContainsKey("preToolUse"), $"Claude settings must not hold lower-case 'preToolUse'. Written: {content}");
        Assert.False(hooks.ContainsKey("postToolUse"), $"Claude settings must not hold lower-case 'postToolUse'. Written: {content}");

        string[] expectedOwned = ["/hooks/PreToolUse/0", "/hooks/PostToolUse/0"];
        Assert.Equal(expectedOwned, owned.Select(entry => entry.Location));
    }

    /// <summary>
    /// A file that already has a PascalCase <c>PreToolUse</c> with a user entry gets the Squad entry
    /// appended after it, and <c>PostToolUse</c> is created. The PreToolUse assertion passes today;
    /// the absent-lower-case assertion is the regression.
    /// </summary>
    [Fact]
    public void SpliceContent_ClaudeUserPreToolUseOnly_AppendsSquadAndCreatesPascalPostToolUse()
    {
        string existing = "{\"hooks\":{\"PreToolUse\":[" + UserGroupJson + "]}}";

        (string content, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Claude,
            existing,
            [Node(SquadGroupJson)],
            [Node(SquadGroupJson)]);

        JsonObject hooks = Hooks(content);
        JsonArray pre = hooks["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("Bash", pre[0]!["matcher"]!.GetValue<string>());
        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, pre[1]), $"The Squad entry must follow the user entry. Written: {content}");

        Assert.Single(Assert.IsType<JsonArray>(hooks["PostToolUse"]));
        Assert.False(hooks.ContainsKey("preToolUse"), $"Splice must not write lower-case 'preToolUse'. Written: {content}");
        Assert.False(hooks.ContainsKey("postToolUse"), $"Splice must not write lower-case 'postToolUse'. Written: {content}");
    }

    /// <summary>
    /// Legacy file: Squad's PreToolUse entry and a lower-case <c>postToolUse</c> that holds only
    /// Squad's entry. The splice must retire the emptied lower-case container and write exactly one
    /// Squad entry under each PascalCase container, with no duplicates.
    /// </summary>
    [Fact]
    public void SpliceContent_ClaudeLegacyLowerCasePostOnlySquad_MigratesToPascalAndRemovesLowerCase()
    {
        string existing = "{\"hooks\":{\"PreToolUse\":[" + SquadGroupJson + "],\"postToolUse\":[" + SquadGroupJson + "]}}";

        (string content, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Claude,
            existing,
            [Node(SquadGroupJson)],
            [Node(SquadGroupJson)]);

        JsonObject hooks = Hooks(content);
        Assert.False(hooks.ContainsKey("postToolUse"), $"The emptied lower-case 'postToolUse' must be removed. Written: {content}");
        JsonArray post = Assert.IsType<JsonArray>(hooks["PostToolUse"]);
        Assert.Single(post);
        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, post[0]));
        Assert.Single(Assert.IsType<JsonArray>(hooks["PreToolUse"]));
        Assert.Equal(2, CountSquadEntries(content));
    }

    /// <summary>
    /// Legacy file: a lower-case <c>postToolUse</c> holding a user entry and a Squad entry. The user
    /// entry stays where the user put it; Squad's entry moves to the PascalCase container.
    /// </summary>
    [Fact]
    public void SpliceContent_ClaudeLegacyLowerCasePostWithUserEntry_KeepsUserEntryAndMovesSquadToPascal()
    {
        string existing = "{\"hooks\":{\"postToolUse\":[" + UserGroupJson + "," + SquadGroupJson + "]}}";

        (string content, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Claude,
            existing,
            [Node(SquadGroupJson)],
            [Node(SquadGroupJson)]);

        JsonObject hooks = Hooks(content);
        JsonArray lowerPost = hooks["postToolUse"]!.AsArray();
        Assert.Single(lowerPost);
        Assert.Equal("Bash", lowerPost[0]!["matcher"]!.GetValue<string>());

        JsonArray pascalPost = Assert.IsType<JsonArray>(hooks["PostToolUse"]);
        Assert.Single(pascalPost);
        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, pascalPost[0]), $"Squad's post entry must sit under 'PostToolUse'. Written: {content}");
        Assert.Equal(2, CountSquadEntries(content));
    }

    /// <summary>
    /// Removal on a file that holds Squad entries under both spellings must clear every one of
    /// them, and must not touch the user's entry under the lower-case post container.
    /// </summary>
    [Fact]
    public void RemoveContent_ClaudeSquadUnderBothSpellings_RemovesAllSquadAndKeepsUserEntry()
    {
        string existing = "{\"hooks\":{\"PreToolUse\":[" + SquadGroupJson + "],\"preToolUse\":[" + SquadGroupJson
            + "],\"postToolUse\":[" + UserGroupJson + "]}}";

        string content = SquadHookJsonBlock.RemoveContent(SquadHookBlockFormat.Claude, existing);

        Assert.Equal(0, CountSquadEntries(content));
        JsonArray post = Hooks(content)["postToolUse"]!.AsArray();
        Assert.Single(post);
        Assert.Equal("Bash", post[0]!["matcher"]!.GetValue<string>());
    }

    /// <summary>
    /// A migrated file whose digests were recorded under the legacy lower-case pointers must not
    /// read as drift: the two spellings name the same container when drift re-anchors an entry.
    /// </summary>
    [Fact]
    public void FindDrift_ClaudeMigratedFileWithLegacyPostPointer_ReportsNoDrift()
    {
        string migrated = "{\"hooks\":{\"PreToolUse\":[" + SquadGroupJson + "],\"PostToolUse\":[" + SquadGroupJson + "]}}";
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["/hooks/PreToolUse/0"] = Digest(SquadGroupJson),
            ["/hooks/postToolUse/0"] = Digest(SquadGroupJson),
        };

        IReadOnlyList<SquadHookDrift> drift = SquadHookJsonBlock.FindDrift(SquadHookBlockFormat.Claude, migrated, expected);

        Assert.Empty(drift);
    }

    /// <summary>
    /// The same migrated file with Squad's PreToolUse entry edited in place must report exactly that
    /// entry as modified. The legacy post pointer still anchors to its PascalCase twin and is not
    /// reported as missing.
    /// </summary>
    [Fact]
    public void FindDrift_ClaudeMigratedFileWithEditedSquadEntry_ReportsThatEntryAsModified()
    {
        string edited = "{\"hooks\":{\"PreToolUse\":[{\"matcher\":\"^(Bash)$\",\"hooks\":[{\"type\":\"command\",\"command\":\"kyber-weave-arbiter hook --harness claude --scope global\"}]}],\"PostToolUse\":["
            + SquadGroupJson + "]}}";
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["/hooks/PreToolUse/0"] = Digest(SquadGroupJson),
            ["/hooks/postToolUse/0"] = Digest(SquadGroupJson),
        };

        IReadOnlyList<SquadHookDrift> drift = SquadHookJsonBlock.FindDrift(SquadHookBlockFormat.Claude, edited, expected);

        SquadHookDrift reported = Assert.Single(drift);
        Assert.Equal("/hooks/PreToolUse/0", reported.Location);
        Assert.Equal("modified", reported.Reason);
    }

    /// <summary>
    /// Cursor keeps its lower-case containers byte for byte. The expected text is what the splice
    /// wrote before the Claude fix; a change here means the Cursor format moved and must be decided.
    /// </summary>
    [Fact]
    public void SpliceContent_CursorVersionOneWithUserPreToolUse_KeepsLowerCaseContainersByteForByte()
    {
        string existing = "{\"version\":1,\"hooks\":{\"preToolUse\":[" + CursorUserJson + "],\"postToolUse\":[]}}";

        (string content, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Cursor,
            existing,
            [Node(CursorSquadPreJson)],
            [Node(CursorSquadPostJson)]);

        const string expected = """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "command": "user-linter --fast",
                    "matcher": "Edit",
                    "timeout": 3,
                    "failClosed": false
                  },
                  {
                    "command": "kyber-weave-arbiter hook --harness cursor",
                    "matcher": "Task",
                    "timeout": 5,
                    "failClosed": true
                  }
                ],
                "postToolUse": [
                  {
                    "command": "kyber-weave-arbiter hook --harness cursor",
                    "matcher": "Edit",
                    "timeout": 10,
                    "failClosed": false
                  }
                ]
              }
            }
            """;

        Assert.Equal(expected + "\n", content);
    }

    /// <summary>
    /// Cursor on an absent file writes lower-case containers, as it does today. The Claude fix must
    /// not reach this path.
    /// </summary>
    [Fact]
    public void SpliceContent_CursorNullSettings_WritesLowerCaseContainersByteForByte()
    {
        (string content, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Cursor,
            null,
            [Node(CursorSquadPreJson)],
            [Node(CursorSquadPostJson)]);

        const string expected = """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "command": "kyber-weave-arbiter hook --harness cursor",
                    "matcher": "Task",
                    "timeout": 5,
                    "failClosed": true
                  }
                ],
                "postToolUse": [
                  {
                    "command": "kyber-weave-arbiter hook --harness cursor",
                    "matcher": "Edit",
                    "timeout": 10,
                    "failClosed": false
                  }
                ]
              }
            }
            """;

        Assert.Equal(expected + "\n", content);
    }
}
