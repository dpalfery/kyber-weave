using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The bounded JSON splice (5.4–5.7): given a document's bytes and one entry, where does the entry
/// go, and which bytes around it must survive untouched?
/// </summary>
/// <remarks>
/// <para>
/// Every case builds bytes rather than a document object, because the guarantee under test is about
/// bytes: no existing formatting, comment, key order or byte-order mark may move (5.6). The
/// preservation assertion is a recomposition — the plan's own insertion offset and inserted bytes
/// must reproduce the result exactly from the input — so a splice that re-serialized the document
/// fails here even when its output would be semantically identical.
/// </para>
/// <para>
/// The lenient read is part of the contract (design DD9): a commented, trailing-comma document is a
/// valid target, and its comments survive because the write never rewrites them. Every refusal must
/// leave the input byte-identical, because a splice that mutates on the way to refusing is a
/// partial write wearing a refusal's clothes.
/// </para>
/// </remarks>
public sealed class JsonServerSpliceTests
{
    private const string ServerName = "kyber-weave";

    private const string VsCodeServersKey = "servers";

    private const string OpenCodeServersKey = "mcp";

    /// <summary>
    /// The UTF-8 byte-order mark. It is bytes in the document, not an encoding detail: a splice that
    /// drops it rewrites a file the operator did not ask it to touch.
    /// </summary>
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>The VS Code entry body, exactly as the runbook documents it (R5.11).</summary>
    private const string VsCodeEntry = """
        {
          "type": "stdio",
          "command": "kyber-weave-mcp",
          "args": ["--repo-root", "${workspaceFolder}"],
          "env": {
            "KYBER_WEAVE_REPO_ROOT": "${workspaceFolder}"
          }
        }
        """;

    /// <summary>
    /// The opencode entry body: one array for command and arguments, and <c>environment</c> rather
    /// than <c>env</c> — the two quirks that make it a distinct recipe rather than a copy.
    /// </summary>
    private const string OpenCodeEntry = """
        {
          "type": "local",
          "command": ["kyber-weave-mcp", "--repo-root", "."],
          "enabled": true,
          "environment": {
            "KYBER_WEAVE_REPO_ROOT": "."
          }
        }
        """;

    public static TheoryData<string> MalformedDocuments() => new()
    {
        string.Empty,
        "   ",
        "{",
        """{"servers": }""",
        """{"servers": {}} trailing"""
    };

    /// <summary>
    /// Documents whose root, or whose server container, is not a JSON object. A harness would never
    /// load them, so writing into one is the "structurally foreign" refusal (5.7).
    /// </summary>
    public static TheoryData<string> ForeignDocuments() => new()
    {
        "[]",
        "\"hello\"",
        """{"servers": []}""",
        """{"servers": "none"}"""
    };

    /// <summary>
    /// The ordinary case: a file that already holds another server, a hand-written comment, a
    /// trailing comma and a byte-order mark. The entry goes in; nothing else moves.
    /// </summary>
    [Fact]
    public void ANonEmptyContainerGainsTheEntryAndKeepsEveryOtherByte()
    {
        byte[] original = WithByteOrderMark(Utf8("""
            {
              // another server, added by hand
              "servers": {
                "other": {
                  "command": "other-mcp"
                },
              },
            }
            """));

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [VsCodeServersKey], ServerName, VsCodeEntry);

        AssertOnlyTheInsertedBytesChanged(original, plan);
        Assert.Equal(JsonInsertionPoint.NonEmptyContainer, plan.InsertionPoint);

        Assert.True(
            plan.Document.AsSpan().StartsWith(Utf8Bom),
            "the byte-order mark must still be the first thing in the document (DD9)");

        using JsonDocument parsed = ParseLenient(plan.Document.AsMemory(3));
        JsonElement servers = parsed.RootElement.GetProperty(VsCodeServersKey);
        Assert.True(servers.TryGetProperty("other", out _), "the server already in the file must survive");
        AssertEntryIs(servers.GetProperty(ServerName), VsCodeEntry, "the inserted entry");
        Assert.Contains(
            "// another server, added by hand",
            Encoding.UTF8.GetString(plan.Document),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty container takes the entry with no comma, and the result must still be valid JSON —
    /// the document a harness is about to read.
    /// </summary>
    [Fact]
    public void AnEmptyContainerGainsTheEntryAndStillParses()
    {
        byte[] original = Utf8("""{"servers": {}}""");

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [VsCodeServersKey], ServerName, VsCodeEntry);

        AssertOnlyTheInsertedBytesChanged(original, plan);
        Assert.Equal(JsonInsertionPoint.EmptyContainer, plan.InsertionPoint);

        using JsonDocument parsed = ParseLenient(plan.Document);
        AssertEntryIs(
            parsed.RootElement.GetProperty(VsCodeServersKey).GetProperty(ServerName),
            VsCodeEntry,
            "the inserted entry");
    }

    /// <summary>
    /// The container is absent: the whole key path is inserted after the root object's opening
    /// brace, and the members that were already there keep their bytes and their meaning.
    /// </summary>
    [Fact]
    public void AnAbsentContainerIsInsertedAfterTheRootBrace()
    {
        byte[] original = Utf8("""
            {
              "$schema": "https://opencode.ai/config.json"
            }
            """);

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [OpenCodeServersKey], ServerName, OpenCodeEntry);

        AssertOnlyTheInsertedBytesChanged(original, plan);
        Assert.Equal(JsonInsertionPoint.AbsentContainer, plan.InsertionPoint);

        using JsonDocument parsed = ParseLenient(plan.Document);
        Assert.Equal(
            "https://opencode.ai/config.json",
            parsed.RootElement.GetProperty("$schema").GetString());
        AssertEntryIs(
            parsed.RootElement.GetProperty(OpenCodeServersKey).GetProperty(ServerName),
            OpenCodeEntry,
            "the inserted entry");
    }

    /// <summary>
    /// An empty root object is the case a naive "insert the key and a comma" gets wrong: the result
    /// would carry a trailing comma, which the harness's own strict parser rejects. The document we
    /// write has to be one the harness can load.
    /// </summary>
    [Fact]
    public void AnEmptyRootObjectGainsTheContainerWithoutATrailingComma()
    {
        byte[] original = Utf8("{}");

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [OpenCodeServersKey], ServerName, OpenCodeEntry);

        AssertOnlyTheInsertedBytesChanged(original, plan);
        Assert.Equal(JsonInsertionPoint.AbsentContainer, plan.InsertionPoint);

        using JsonDocument parsed = JsonDocument.Parse(plan.Document);
        AssertEntryIs(
            parsed.RootElement.GetProperty(OpenCodeServersKey).GetProperty(ServerName),
            OpenCodeEntry,
            "the inserted entry");
    }

    /// <summary>The byte-order mark survives an insert, and only the insert is added (DD9).</summary>
    [Fact]
    public void TheByteOrderMarkSurvivesTheInsert()
    {
        byte[] original = WithByteOrderMark(Utf8("""{"servers": {}}"""));

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [VsCodeServersKey], ServerName, VsCodeEntry);

        AssertOnlyTheInsertedBytesChanged(original, plan);
        Assert.True(
            plan.Document.AsSpan().StartsWith(Utf8Bom),
            "a BOM preserved means the first three bytes are still the BOM");
        Assert.True(
            plan.InsertOffset >= Utf8Bom.Length,
            $"the insert must follow the BOM, never land inside it, but started at {plan.InsertOffset}");

        using JsonDocument parsed = ParseLenient(plan.Document.AsMemory(3));
        Assert.True(parsed.RootElement.TryGetProperty(VsCodeServersKey, out _));
    }

    /// <summary>
    /// Comments and trailing commas are tolerated on the read side and preserved on the write side —
    /// the file stays a document its owner can still edit, comments and all (5.6, DD9).
    /// </summary>
    [Fact]
    public void CommentsAndTrailingCommasAreToleratedAndPreserved()
    {
        byte[] original = Utf8("""
            {
              // the key VS Code reads, not mcpServers
              "servers": {
                /* a second server, hand-configured */
                "other": {
                  "args": ["one", "two",],
                },
              },
            }
            """);

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [VsCodeServersKey], ServerName, VsCodeEntry);

        AssertOnlyTheInsertedBytesChanged(original, plan);

        string text = Encoding.UTF8.GetString(plan.Document);
        Assert.Contains("// the key VS Code reads, not mcpServers", text, StringComparison.Ordinal);
        Assert.Contains("/* a second server, hand-configured */", text, StringComparison.Ordinal);
        Assert.Contains("\"args\": [\"one\", \"two\",]", text, StringComparison.Ordinal);

        using JsonDocument parsed = ParseLenient(plan.Document);
        Assert.True(parsed.RootElement.GetProperty(VsCodeServersKey).TryGetProperty("other", out _));
    }

    /// <summary>
    /// An entry already present is refused rather than merged or overwritten (5.5): the document is
    /// returned byte-identical and the plan names the server it would have clobbered.
    /// </summary>
    [Fact]
    public void AnExistingServerNameIsRefusedAndChangesNothing()
    {
        byte[] original = Utf8("""
            {
              "servers": {
                "kyber-weave": {
                  "command": "kyber-weave-mcp"
                }
              }
            }
            """);

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [VsCodeServersKey], ServerName, VsCodeEntry);

        AssertRefused(plan, JsonSpliceOutcome.ServerAlreadyPresent, original);
        Assert.Contains(ServerName, plan.Message ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void AMalformedDocumentIsRefusedAndChangesNothing(string document)
    {
        byte[] original = Utf8(document);

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [VsCodeServersKey], ServerName, VsCodeEntry);

        AssertRefused(plan, JsonSpliceOutcome.MalformedDocument, original);
    }

    [Theory]
    [MemberData(nameof(ForeignDocuments))]
    public void AForeignDocumentIsRefusedAndChangesNothing(string document)
    {
        byte[] original = Utf8(document);

        JsonSplicePlan plan = JsonServerSplice.Plan(original, [VsCodeServersKey], ServerName, VsCodeEntry);

        AssertRefused(plan, JsonSpliceOutcome.ForeignDocument, original);
    }

    /// <summary>
    /// The plan is the whole change: its offset and inserted bytes reproduce the result exactly
    /// from the input, so every byte outside the insert is provably untouched (5.6).
    /// </summary>
    private static void AssertOnlyTheInsertedBytesChanged(byte[] input, JsonSplicePlan plan)
    {
        Assert.Equal(JsonSpliceOutcome.Inserted, plan.Outcome);
        Assert.InRange(plan.InsertOffset, 0, input.Length);
        Assert.True(plan.InsertedBytes.Length > 0, "an insert must carry the bytes it adds");

        byte[] recomposed =
        [
            .. input[..plan.InsertOffset],
            .. plan.InsertedBytes,
            .. input[plan.InsertOffset..]
        ];

        Assert.Equal(recomposed, plan.Document);
    }

    private static void AssertRefused(JsonSplicePlan plan, JsonSpliceOutcome expected, byte[] original)
    {
        Assert.Equal(expected, plan.Outcome);
        Assert.Equal(original, plan.Document);
        Assert.Empty(plan.InsertedBytes);
        Assert.Equal(-1, plan.InsertOffset);
        Assert.False(
            string.IsNullOrWhiteSpace(plan.Message),
            $"{expected} must say what it refused and why");
    }

    /// <summary>
    /// Compares an entry body as JSON so the assertion pins the recipe's keys and values, not the
    /// indentation the write happened to choose.
    /// </summary>
    private static void AssertEntryIs(JsonElement entry, string expectedJson, string context) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(entry.GetRawText()), JsonNode.Parse(expectedJson)),
            $"{context}: expected {expectedJson}, but found {entry.GetRawText()}");

    private static JsonDocument ParseLenient(ReadOnlyMemory<byte> document) =>
        JsonDocument.Parse(
            document,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] WithByteOrderMark(byte[] document) => [.. Utf8Bom, .. document];
}
