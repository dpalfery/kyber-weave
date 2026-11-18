using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
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
    private static readonly string Base = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "hook-json");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private SquadHookJsonFile Hooks =>
        new(_files, new FakeProcessEnvironment(currentDirectory: Base));

    private static string Harness(SquadHookBlockFormat format) =>
        format switch
        {
            SquadHookBlockFormat.Cursor => "cursor",
            SquadHookBlockFormat.Claude => "claude",
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

    [Fact]
    public void SpliceFile_SubagentStopForAnotherFormat_NamesTheCallerBug()
    {
        string path = Path.Combine(Base, "kw-hook.json");
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(SquadHookBlockFormat.Cursor);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => Hooks.SpliceFile(SquadHookBlockFormat.Cursor, path, pre, post, pre));

        Assert.Contains("Only the Factory", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not parse", failure.Message, StringComparison.Ordinal);
        Assert.False(_files.FileExists(path));
    }

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
            SquadHookBlockFormat.Codex or SquadHookBlockFormat.Claude => new JsonObject
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

    [Theory]
    [InlineData(NormalizationForm.FormD)]
    [InlineData(NormalizationForm.FormC)]
    public void SpliceAndRemoveActOnTheExistingFileWhateverUnicodeFormItsFolderNameUses(NormalizationForm form)
    {
        // The in-memory file system compares names ordinally, as ext4 and NTFS do, so a decomposed name and its
        // composed twin are two different folders.
        const SquadHookBlockFormat format = SquadHookBlockFormat.Cursor;
        string path = Path.Combine(Base, "caf\u00e9".Normalize(form), SquadHookJsonBlock.RelativePath(format));
        string twin = Path.Combine(
            Base,
            "caf\u00e9".Normalize(form == NormalizationForm.FormD ? NormalizationForm.FormC : NormalizationForm.FormD),
            SquadHookJsonBlock.RelativePath(format));
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        string userContent = UserFileContent(format);
        _files.WriteAllText(path, userContent);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);

        Hooks.SpliceFile(format, path, pre, post);

        Assert.NotEqual(userContent, _files.ReadAllText(path));
        Assert.False(_files.FileExists(twin));

        Hooks.RemoveFile(format, path);

        Assert.Equal(
            JsonNode.Parse(userContent)!.ToJsonString(),
            JsonNode.Parse(_files.ReadAllText(path))!.ToJsonString());
    }

    private static JsonNode Clone(JsonNode node) =>
        JsonNode.Parse(node.ToJsonString())!;

    private static string HookFile(SquadHookBlockFormat format) =>
        Path.Combine(Base, SquadHookJsonBlock.RelativePath(format));

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
        string path = HookFile(format);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);

        IReadOnlyList<SquadHookOwnedEntry> owned = Hooks.SpliceFile(format, path, pre, post);

        Assert.True(_files.FileExists(path));
        string content = _files.ReadAllText(path);
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
        string path = HookFile(format);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, string.Empty);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);

        IReadOnlyList<SquadHookOwnedEntry> owned = Hooks.SpliceFile(format, path, pre, post);

        Assert.Equal(2, owned.Count);
        JsonNode root = JsonNode.Parse(_files.ReadAllText(path))!;
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
        string path = HookFile(format);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, UserFileContent(format));
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        (IReadOnlyList<JsonNode> userPre, IReadOnlyList<JsonNode> userPost) = UserEntries(format);

        IReadOnlyList<SquadHookOwnedEntry> owned = Hooks.SpliceFile(format, path, pre, post);

        JsonNode root = JsonNode.Parse(_files.ReadAllText(path))!;
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
        IReadOnlyList<SquadHookOwnedEntry> again = Hooks.SpliceFile(format, path, ManagedEntries(format).Pre, ManagedEntries(format).Post);
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
        string path = HookFile(format);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        string before = UserFileContent(format);
        _files.WriteAllText(path, before);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        Hooks.SpliceFile(format, path, pre, post);

        Hooks.RemoveFile(format, path);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), JsonNode.Parse(_files.ReadAllText(path))));
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Factory)]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void RemoveFile_FileSquadCreated_LeavesNoEmptyContainers(SquadHookBlockFormat format)
    {
        string path = HookFile(format);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        Hooks.SpliceFile(format, path, pre, post);

        Hooks.RemoveFile(format, path);

        JsonObject root = JsonNode.Parse(_files.ReadAllText(path))!.AsObject();
        string[] removed = ["hooks", "PreToolUse", "PostToolUse"];
        Assert.DoesNotContain(root, property => removed.Contains(property.Key, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemoveContent_ClaudeKeepsUserEmptyArrayAndSharedContainerButDropsEmptiedOnes()
    {
        string squad = "{\"matcher\":\"Task\",\"hooks\":[{\"type\":\"command\",\"command\":\"kyber-weave-arbiter hook --harness claude\"}]}";
        string user = "{\"matcher\":\"Bash\",\"hooks\":[{\"type\":\"command\",\"command\":\"user-linter\"}]}";
        string existing = "{\"hooks\":{\"PreToolUse\":[" + squad + "],\"PostToolUse\":[" + user + "," + squad
            + "],\"Stop\":[]}}";

        JsonObject hooks = JsonNode.Parse(SquadHookJsonBlock.RemoveContent(SquadHookBlockFormat.Claude, existing))!["hooks"]!.AsObject();

        Assert.False(hooks.ContainsKey("PreToolUse"));
        Assert.Single(hooks["PostToolUse"]!.AsArray());
        Assert.Empty(hooks["Stop"]!.AsArray());
    }

    [Fact]
    public void RemoveContent_ClaudeUserOwnedEmptyHooksObjectIsKept()
    {
        JsonObject root = JsonNode.Parse(
            SquadHookJsonBlock.RemoveContent(SquadHookBlockFormat.Claude, "{\"hooks\":{},\"theme\":\"dark\"}"))!.AsObject();

        Assert.True(root.ContainsKey("hooks"));
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
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

        string path = HookFile(format);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        Hooks.SpliceFile(format, path, pre, post);
        Hooks.RemoveFile(format, path);
        Assert.True(SquadHookJsonBlock.IsEmpty(format, _files.ReadAllText(path)));
    }

    [Fact]
    public void CursorEntryIsSquadOwnedWhenCommandCarriesHarnessSignature()
    {
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            SquadHookBlockFormat.Cursor, CursorEntry("kyber-weave-arbiter hook --harness cursor --extra")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            SquadHookBlockFormat.Cursor, CursorEntry("kyber-weave-arbiter hook --harness cursor-extra")));
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
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            format, MatcherGroup(harness, "m", $"kyber-weave-arbiter hook --harness {harness} --flag", $"kyber-weave-arbiter hook --harness {harness}")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            format, MatcherGroup(harness, "m", $"kyber-weave-arbiter hook --harness {harness}", "user-tool")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(
            format, MatcherGroup(harness, "m", "user-tool")));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(format, new JsonObject { ["matcher"] = "m", ["hooks"] = new JsonArray() }));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(format, null));
    }

    [Fact]
    public void ClaudeWorkerGuardGroupIsSquadOwnedSoASecondSpliceReplacesIt()
    {
        JsonNode guard = MatcherGroup("claude", "*", "kyber-weave-arbiter worker-guard --harness claude");
        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, guard));

        (string once, _) = SquadHookJsonBlock.SpliceContent(SquadHookBlockFormat.Claude, null, [guard.DeepClone()], []);
        (string twice, _) = SquadHookJsonBlock.SpliceContent(SquadHookBlockFormat.Claude, once, [guard.DeepClone()], []);

        Assert.Equal(once, twice);
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
        string path = HookFile(SquadHookBlockFormat.Codex);
        var mixed = MatcherGroup("codex", "mixed", "kyber-weave-arbiter hook --harness codex", "user-tool");
        var root = new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray { mixed },
                ["PostToolUse"] = new JsonArray(),
            },
        };
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, root.ToJsonString() + "\n");
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(SquadHookBlockFormat.Codex);

        Hooks.SpliceFile(SquadHookBlockFormat.Codex, path, pre, post);

        JsonNode after = JsonNode.Parse(_files.ReadAllText(path))!;
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
        string path = HookFile(format);
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

        Hooks.SpliceFile(format, path, [extra], []);

        string content = _files.ReadAllText(path);
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
        string path = HookFile(format);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);
        IReadOnlyList<SquadHookOwnedEntry> owned = Hooks.SpliceFile(format, path, pre, post);
        var record = owned.ToDictionary(e => e.Location, e => e.Digest, StringComparer.Ordinal);

        Assert.Empty(SquadHookJsonBlock.FindDrift(format, _files.ReadAllText(path), record));

        JsonNode tampered = JsonNode.Parse(_files.ReadAllText(path))!;
        JsonNode? target = Resolve(tampered, owned[0].Location)!;
        if (format == SquadHookBlockFormat.Cursor)
        {
            ((JsonObject)target)["timeout"] = 99;
        }
        else
        {
            ((JsonObject)target)["matcher"] = "hand-edited";
        }

        _files.WriteAllText(path, tampered.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        IReadOnlyList<SquadHookDrift> modified = SquadHookJsonBlock.FindDrift(format, _files.ReadAllText(path), record);
        Assert.Single(modified);
        Assert.Equal(owned[0].Location, modified[0].Location);

        JsonNode removed = JsonNode.Parse(_files.ReadAllText(path))!;
        string pointer = owned[1].Location;
        string container = pointer[..pointer.LastIndexOf('/')];
        int index = int.Parse(pointer[(pointer.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        ((JsonArray)Resolve(removed, container)!).RemoveAt(index);
        _files.WriteAllText(path, removed.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        IReadOnlyList<SquadHookDrift> drift = SquadHookJsonBlock.FindDrift(format, _files.ReadAllText(path), record);
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
        string path = HookFile(format);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        const string Garbage = "{ this is not json";
        _files.WriteAllText(path, Garbage);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(format);

        InvalidOperationException spliceFailure = Assert.Throws<InvalidOperationException>(
            () => Hooks.SpliceFile(format, path, pre, post));
        Assert.Contains(path, spliceFailure.Message, StringComparison.Ordinal);
        Assert.Equal(Garbage, _files.ReadAllText(path));

        InvalidOperationException removeFailure = Assert.Throws<InvalidOperationException>(
            () => Hooks.RemoveFile(format, path));
        Assert.Contains(path, removeFailure.Message, StringComparison.Ordinal);
        Assert.Equal(Garbage, _files.ReadAllText(path));
    }

    /// <summary>
    /// The user's hook file is rewritten in place and nothing else is left beside it: a plain overwrite keeps a
    /// file the user reaches through a link, where a renamed temporary would replace the link.
    /// </summary>
    [Fact]
    public void SpliceOverwritesTheHookFileInPlaceAndLeavesNoSibling()
    {
        string path = HookFile(SquadHookBlockFormat.Codex);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, UserFileContent(SquadHookBlockFormat.Codex));
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(SquadHookBlockFormat.Codex);

        Hooks.SpliceFile(SquadHookBlockFormat.Codex, path, pre, post);

        Assert.Equal([path], _files.EnumerateFiles(Base, "*", recursive: true).ToArray());
    }

    /// <summary>A write that fails leaves the user's earlier hook file as it was, and the failure reaches the caller.</summary>
    [Fact]
    public void SpliceWhoseWriteFailsLeavesTheEarlierHookFile()
    {
        string path = HookFile(SquadHookBlockFormat.Codex);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        string before = UserFileContent(SquadHookBlockFormat.Codex);
        _files.WriteAllText(path, before);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(SquadHookBlockFormat.Codex);
        _files.FailWhen(operation => operation.Name == "WriteAllText", new IOException("disk full"));

        Assert.Throws<IOException>(() => Hooks.SpliceFile(SquadHookBlockFormat.Codex, path, pre, post));

        Assert.Equal(before, _files.ReadAllText(path));
    }

    [Fact]
    public void RemoveWhoseWriteFailsLeavesTheSplicedHookFile()
    {
        string path = HookFile(SquadHookBlockFormat.Codex);
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(SquadHookBlockFormat.Codex);
        Hooks.SpliceFile(SquadHookBlockFormat.Codex, path, pre, post);
        string spliced = _files.ReadAllText(path);
        _files.FailWhen(operation => operation.Name == "WriteAllText", new IOException("disk full"));

        Assert.Throws<IOException>(() => Hooks.RemoveFile(SquadHookBlockFormat.Codex, path));

        Assert.Equal(spliced, _files.ReadAllText(path));
    }

    [Fact]
    public void RemoveOfAMissingFileNeedsNoWork()
    {
        string path = HookFile(SquadHookBlockFormat.Codex);

        Hooks.RemoveFile(SquadHookBlockFormat.Codex, path);

        Assert.False(_files.FileExists(path));
        Assert.False(_files.DirectoryExists(Path.GetDirectoryName(path)!));
    }

    /// <summary>A relative hook path resolves against the process's current directory, as the on-disk write did.</summary>
    [Fact]
    public void SpliceOfARelativePathWritesUnderTheCurrentDirectory()
    {
        (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = ManagedEntries(SquadHookBlockFormat.Codex);
        string relative = Path.Combine("nested", "hooks.json");

        Hooks.SpliceFile(SquadHookBlockFormat.Codex, relative, pre, post);

        Assert.True(_files.FileExists(Path.Combine(Base, relative)));
    }
}

public sealed class SquadHookFormatResolutionTests
{
    private static SquadOwnedBlock Block(string relativePath, string target) =>
        new(relativePath, target, false, []);

    [Fact]
    public void BareGlobalPathResolvesByRecordedTarget()
    {
        Assert.True(SquadHookJsonBlock.TryResolveFormat(Block("hooks.json", "codex"), out SquadHookBlockFormat format));
        Assert.Equal(SquadHookBlockFormat.Codex, format);
    }

    [Theory]
    [InlineData("hooks.json")]
    [InlineData(".agents/hooks.json")]
    public void AntigravityResolvesBothTheGlobalPathAndTheStaleGlobalRecordFromBeforeTheFix(string relativePath)
    {
        Assert.True(SquadHookJsonBlock.TryResolveFormat(Block(relativePath, "antigravity"), out SquadHookBlockFormat format));
        Assert.Equal(SquadHookBlockFormat.Antigravity, format);
    }

    [Fact]
    public void PrefixedProjectPathResolvesByRecordedTarget()
    {
        Assert.True(SquadHookJsonBlock.TryResolveFormat(Block(".codex/hooks.json", "codex"), out SquadHookBlockFormat format));
        Assert.Equal(SquadHookBlockFormat.Codex, format);
    }

    [Fact]
    public void MismatchedTargetAndPathFailsClosed()
    {
        Assert.False(SquadHookJsonBlock.TryResolveFormat(Block(".cursor/hooks.json", "codex"), out _));
    }

    [Fact]
    public void CrossBareNameMismatchFailsClosed()
    {
        Assert.False(SquadHookJsonBlock.TryResolveFormat(Block("hooks.v1.json", "codex"), out _));
        Assert.False(SquadHookJsonBlock.TryResolveFormat(Block("settings.json", "codex"), out _));
        Assert.False(SquadHookJsonBlock.TryResolveFormat(Block("hooks.json", "devin"), out _));
    }

    [Fact]
    public void UnknownTargetFallsBackToPathMatch()
    {
        Assert.True(SquadHookJsonBlock.TryResolveFormat(Block(".cursor/hooks.json", "hal9000"), out SquadHookBlockFormat format));
        Assert.Equal(SquadHookBlockFormat.Cursor, format);
    }

    [Fact]
    public void UnknownTargetAndUnknownPathFailsClosed()
    {
        Assert.False(SquadHookJsonBlock.TryResolveFormat(Block("nope.json", "hal9000"), out _));
    }
}
