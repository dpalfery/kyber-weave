using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the JSON hook-block splice for the five shared-file hook shapes: splice into an
/// absent, empty or user-populated file, user entries keep order and values, removal
/// restores the user's content, signature identification, digest drift, and unparsable
/// input that fails without writing.
/// </summary>
public sealed class SquadHookJsonBlockTests
{
    private static string Harness(SquadHookBlockFormat format) =>
        format switch
        {
            SquadHookBlockFormat.Cursor => "cursor",
            SquadHookBlockFormat.Codex => "codex",
            SquadHookBlockFormat.Factory => "factory",
            SquadHookBlockFormat.Devin => "devin",
            SquadHookBlockFormat.Antigravity => "antigravity",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

    private static JsonObject CursorEntry(string command, string matcher = "Task", int timeout = 5, bool failClosed = true) =>
        new()
        {
            ["command"] = command,
            ["matcher"] = matcher,
            ["timeout"] = timeout,
            ["failClosed"] = failClosed,
        };

    private static JsonObject MatcherGroup(string harness, string matcher, params string[] commands) =>
        new()
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(
                commands.Select(command => (JsonNode)new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                }).ToArray()),
        };

    private static JsonObject ManagedGroup(SquadHookBlockFormat format, string matcher) =>
        MatcherGroup(Harness(format), matcher, $"kyber-weave-arbiter hook --harness {Harness(format)}");

    private static (IReadOnlyList<JsonNode> Pre, IReadOnlyList<JsonNode> Post) ManagedEntries(SquadHookBlockFormat format) =>
        format == SquadHookBlockFormat.Cursor
            ? ([CursorEntry("kyber-weave-arbiter hook --harness cursor")],
                [CursorEntry("kyber-weave-arbiter hook --harness cursor", "Edit", 10, false)])
            : ([ManagedGroup(format, "pre-matcher")], [ManagedGroup(format, "post-matcher")]);

    private static (IReadOnlyList<JsonNode> Pre, IReadOnlyList<JsonNode> Post) UserEntries(SquadHookBlockFormat format) =>
        format == SquadHookBlockFormat.Cursor
            ? ([CursorEntry("user-linter --fast", "Edit", 3, false)],
                [CursorEntry("user-notifier", "*", 7, false)])
            : ([MatcherGroup(Harness(format), "user-pre", "user-tool --pre")],
                [MatcherGroup(Harness(format), "user-post", "user-tool --post")]);

    private static string UserFileContent(SquadHookBlockFormat format)
    {
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = UserEntries(format);
        JsonObject root = format switch
        {
            SquadHookBlockFormat.Cursor => new JsonObject
            {
                ["version"] = 1,
                ["hooks"] = new JsonObject
                {
                    ["preToolUse"] = new JsonArray(pre.Select(Clone).ToArray()),
                    ["postToolUse"] = new JsonArray(post.Select(Clone).ToArray()),
                },
            },
            SquadHookBlockFormat.Codex => new JsonObject
            {
                ["hooks"] = new JsonObject
                {
                    ["PreToolUse"] = new JsonArray(pre.Select(Clone).ToArray()),
                    ["PostToolUse"] = new JsonArray(post.Select(Clone).ToArray()),
                },
            },
            SquadHookBlockFormat.Factory or SquadHookBlockFormat.Devin => new JsonObject
            {
                ["PreToolUse"] = new JsonArray(pre.Select(Clone).ToArray()),
                ["PostToolUse"] = new JsonArray(post.Select(Clone).ToArray()),
                ["unrelated"] = 42,
            },
            SquadHookBlockFormat.Antigravity => new JsonObject
            {
                ["other-group"] = new JsonObject
                {
                    ["PreToolUse"] = new JsonArray(pre.Select(Clone).ToArray()),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
        return root.ToJsonString() + "\n";
    }

    private static JsonNode Clone(JsonNode node) =>
        JsonNode.Parse(node.ToJsonString())!;

    private static string HookFile(TempDirectory fixture, SquadHookBlockFormat format) =>
        Path.Combine(fixture.Path, SquadHookJsonBlock.RelativePath(format));

    private static JsonNode? Resolve(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        foreach (string raw in pointer.Split('/').Skip(1))
        {
            string segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray arr when int.TryParse(segment, out int index) && index >= 0 && index < arr.Count => arr[index],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void SpliceIntoAbsentFileCreatesContainersAndAppendsManaged(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);

        IReadOnlyList<SquadHookOwnedEntry> owned = SquadHookJsonBlock.SpliceFile(format, path, pre, post);

        Assert.True(File.Exists(path));
        string content = File.ReadAllText(path);
        Assert.EndsWith("\n", content, StringComparison.Ordinal);
        JsonNode root = JsonNode.Parse(content)!;
        Assert.Equal(2, owned.Count);
        foreach (SquadHookOwnedEntry entry in owned)
        {
            JsonNode? node = Resolve(root, entry.Location);
            Assert.NotNull(node);
            Assert.Equal(SquadHookJsonBlock.CanonicalDigest(node), entry.Digest);
        }
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void SpliceIntoEmptyFileKeepsNothingAndWritesManaged(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);

        IReadOnlyList<SquadHookOwnedEntry> owned = SquadHookJsonBlock.SpliceFile(format, path, pre, post);

        Assert.Equal(2, owned.Count);
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (SquadHookOwnedEntry entry in owned)
        {
            Assert.Equal(entry.Digest, SquadHookJsonBlock.CanonicalDigest(Resolve(root, entry.Location)!));
        }
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void SpliceIntoUserFileKeepsUserOrderAndValuesAndAppendsManaged(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, UserFileContent(format));
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        (IReadOnlyList<JsonNode> userPre, IReadOnlyList<JsonNode> userPost) = UserEntries(format);

        IReadOnlyList<SquadHookOwnedEntry> owned = SquadHookJsonBlock.SpliceFile(format, path, pre, post);

        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (SquadHookOwnedEntry entry in owned)
        {
            Assert.Equal(SquadHookJsonBlock.CanonicalDigest(Resolve(root, entry.Location)!), entry.Digest);
        }

        if (format == SquadHookBlockFormat.Antigravity)
        {
            JsonNode group = root["kyber-arbiter"]!;
            Assert.True(JsonNode.DeepEquals(Clone(pre[0]), Resolve(group, "/PreToolUse/0")));
            Assert.True(JsonNode.DeepEquals(Clone(post[0]), Resolve(group, "/PostToolUse/0")));
            JsonNode other = root["other-group"]!;
            Assert.True(JsonNode.DeepEquals(Clone(userPre[0]), Resolve(other, "/PreToolUse/0")));
        }
        else
        {
            string prePointer = owned[0].Location;
            string postPointer = owned[1].Location;
            string preContainer = prePointer[..prePointer.LastIndexOf('/')];
            string postContainer = postPointer[..postPointer.LastIndexOf('/')];
            JsonArray preArray = (JsonArray)Resolve(root, preContainer)!;
            JsonArray postArray = (JsonArray)Resolve(root, postContainer)!;
            Assert.True(JsonNode.DeepEquals(Clone(userPre[0]), preArray[0]));
            Assert.True(JsonNode.DeepEquals(Clone(userPost[0]), postArray[0]));
            Assert.True(JsonNode.DeepEquals(Clone(pre[0]), preArray[^1]));
            Assert.True(JsonNode.DeepEquals(Clone(post[0]), postArray[^1]));
        }

        // A second splice replaces Squad's entries instead of duplicating them.
        IReadOnlyList<SquadHookOwnedEntry> again = SquadHookJsonBlock.SpliceFile(format, path, ManagedEntries(format).Pre, ManagedEntries(format).Post);
        Assert.Equal(owned.Count, again.Count);
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void RemoveRestoresUserContent(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string before = UserFileContent(format);
        File.WriteAllText(path, before);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        SquadHookJsonBlock.SpliceFile(format, path, pre, post);

        SquadHookJsonBlock.RemoveFile(format, path);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), JsonNode.Parse(File.ReadAllText(path))));
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void IsEmptyReportsWhetherAnyHookRemains(SquadHookBlockFormat format)
    {
        Assert.True(SquadHookJsonBlock.IsEmpty(format, null));
        Assert.False(SquadHookJsonBlock.IsEmpty(format, UserFileContent(format)));

        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        SquadHookJsonBlock.SpliceFile(format, path, pre, post);
        SquadHookJsonBlock.RemoveFile(format, path);
        Assert.True(SquadHookJsonBlock.IsEmpty(format, File.ReadAllText(path)));
    }

    [Fact]
    public void CursorEntryIsSquadOwnedWhenCommandCarriesHarnessSignature()
    {
        Assert.True(SquadHookJsonBlock.IsSquadEntry(
            SquadHookBlockFormat.Cursor, CursorEntry("kyber-weave-arbiter hook --harness cursor --extra")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            SquadHookBlockFormat.Cursor, CursorEntry("user-linter --fast")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            SquadHookBlockFormat.Cursor, CursorEntry("kyber-weave-arbiter hook --harness codex")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Cursor, new JsonObject()));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Cursor, null));
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Codex, "codex")]
    [InlineData(SquadHookBlockFormat.Factory, "factory")]
    [InlineData(SquadHookBlockFormat.Devin, "devin")]
    public void MatcherGroupIsSquadOwnedOnlyWhenEveryHookCarriesHarnessSignature(
        SquadHookBlockFormat format, string harness)
    {
        Assert.True(SquadHookJsonBlock.IsSquadEntry(
            format, MatcherGroup(harness, "m", $"kyber-weave-arbiter hook --harness {harness}")));
        Assert.True(SquadHookJsonBlock.IsSquadEntry(
            format, MatcherGroup(harness, "m", $"kyber-weave-arbiter hook --harness {harness} --flag", $"kyber-weave-arbiter hook --harness {harness}")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            format, MatcherGroup(harness, "m", $"kyber-weave-arbiter hook --harness {harness}", "user-tool")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            format, MatcherGroup(harness, "m", "user-tool")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(format, new JsonObject { ["matcher"] = "m", ["hooks"] = new JsonArray() }));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(format, null));
    }

    [Fact]
    public void AntigravityGroupIsOwnedByKey()
    {
        Assert.True(SquadHookJsonBlock.IsSquadEntry(
            SquadHookBlockFormat.Antigravity, ManagedGroup(SquadHookBlockFormat.Antigravity, "m")));
        Assert.True(SquadHookJsonBlock.IsSquadEntry(
            SquadHookBlockFormat.Antigravity, MatcherGroup("antigravity", "m", "user-tool")));
    }

    [Fact]
    public void MixedMatcherGroupIsKeptAsUserContent()
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, SquadHookBlockFormat.Codex);
        var mixed = MatcherGroup("codex", "mixed", "kyber-weave-arbiter hook --harness codex", "user-tool");
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray { mixed },
                ["PostToolUse"] = new JsonArray(),
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString() + "\n");
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(SquadHookBlockFormat.Codex);

        SquadHookJsonBlock.SpliceFile(SquadHookBlockFormat.Codex, path, pre, post);

        JsonNode after = JsonNode.Parse(File.ReadAllText(path))!;
        var array = (JsonArray)after["hooks"]!["PreToolUse"]!;
        Assert.Equal(2, array.Count);
        Assert.True(JsonNode.DeepEquals(mixed, array[0]));
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void SpliceWritesOnlyDocumentedFields(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        JsonObject extra = format == SquadHookBlockFormat.Cursor
            ? new JsonObject
            {
                ["command"] = "kyber-weave-arbiter hook --harness cursor",
                ["matcher"] = "Task",
                ["timeout"] = 5,
                ["failClosed"] = true,
                ["sentinel"] = "kyber-managed",
                ["bogus"] = 1,
            }
            : new JsonObject
            {
                ["matcher"] = "m",
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"kyber-weave-arbiter hook --harness {Harness(format)}",
                        ["timeout"] = 5,
                        ["sentinel"] = "kyber-managed",
                        ["bogus"] = 1,
                    },
                },
                ["sentinel"] = "kyber-managed",
            };

        SquadHookJsonBlock.SpliceFile(format, path, [extra], []);

        string content = File.ReadAllText(path);
        Assert.DoesNotContain("sentinel", content, StringComparison.Ordinal);
        Assert.DoesNotContain("bogus", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void DriftReportsModifiedAndMissingOwnedEntries(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        IReadOnlyList<SquadHookOwnedEntry> owned = SquadHookJsonBlock.SpliceFile(format, path, pre, post);
        var record = owned.ToDictionary(e => e.Location, e => e.Digest, StringComparer.Ordinal);

        Assert.Empty(SquadHookJsonBlock.FindDrift(format, File.ReadAllText(path), record));

        JsonNode tampered = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode? target = Resolve(tampered, owned[0].Location)!;
        if (format == SquadHookBlockFormat.Cursor)
        {
            ((JsonObject)target)["timeout"] = 99;
        }
        else
        {
            ((JsonObject)target)["matcher"] = "hand-edited";
        }

        File.WriteAllText(path, tampered.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        IReadOnlyList<SquadHookDrift> modified = SquadHookJsonBlock.FindDrift(format, File.ReadAllText(path), record);
        Assert.Single(modified);
        Assert.Equal(owned[0].Location, modified[0].Location);

        JsonNode removed = JsonNode.Parse(File.ReadAllText(path))!;
        string pointer = owned[1].Location;
        string container = pointer[..pointer.LastIndexOf('/')];
        int index = int.Parse(pointer[(pointer.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        ((JsonArray)Resolve(removed, container)!).RemoveAt(index);
        File.WriteAllText(path, removed.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        IReadOnlyList<SquadHookDrift> drift = SquadHookJsonBlock.FindDrift(format, File.ReadAllText(path), record);
        Assert.Equal(2, drift.Count);
        Assert.Contains(drift, d => string.Equals(d.Location, pointer, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void UnparsableFileFailsNamingTheFileAndWritesNothing(SquadHookBlockFormat format)
    {
        using TempDirectory fixture = new();
        string path = HookFile(fixture, format);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string Garbage = "{ this is not json";
        File.WriteAllText(path, Garbage);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);

        InvalidOperationException spliceFailure = Assert.Throws<InvalidOperationException>(
            () => SquadHookJsonBlock.SpliceFile(format, path, pre, post));
        Assert.Contains(path, spliceFailure.Message, StringComparison.Ordinal);
        Assert.Equal(Garbage, File.ReadAllText(path));

        InvalidOperationException removeFailure = Assert.Throws<InvalidOperationException>(
            () => SquadHookJsonBlock.RemoveFile(format, path));
        Assert.Contains(path, removeFailure.Message, StringComparison.Ordinal);
        Assert.Equal(Garbage, File.ReadAllText(path));
    }
}
