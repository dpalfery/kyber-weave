using System.Globalization;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Kilo;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>
/// Regression coverage for the <c>content_json</c> non-string hardening, ported onto the relational
/// store. The <c>content_json</c> column is gone: content now arrives as normalized
/// <c>content_part</c> rows, so the legacy <c>ParseContentColumn</c> cases are replayed as a mapper
/// plus <c>WriteBatch</c> plus reader round-trip. Tool outputs holding non-string JSON values (a
/// boolean, a number) must come back as text, and nested values as withheld JSON, without throwing.
/// </summary>
/// <remarks>
/// Dropped legacy-only assertions: a column that is not a JSON object reading as empty, and a
/// corrupt column reading as empty instead of failing the listing, have no equivalent because there
/// is no JSON column anymore (a span with no captured payload yields no content rows, and malformed
/// input is rejected at write validation or skipped by the mapper).
/// </remarks>
public class ContentColumnTests
{
    private const string SessionKey = "sess-kilo-content";

    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    // The non-string values copied from a claude-code interaction row in a real 272k-record store.
    // That row made every analytics operation throw "The JSON value could not be converted to
    // System.String"; here the same values ride Kilo tool outputs the way a mapper now receives
    // them: false, true, 46, 0.0125, and 0 beside the text, plus a nested object and array.
    private static string BuildExport()
    {
        long tick = new DateTimeOffset(2026, 11, 19, 11, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return """
            {
              "session_id": "sess-kilo-content",
              "title": "Content regression",
              "messages": [
                {"id": "msg-a1", "session_id": "sess-kilo-content", "time_created": __TS__, "data": {"role": "assistant", "modelID": "kilo-model-1", "tokens": {"input": 100, "output": 20}}}
              ],
              "parts": [
                {"id": "part-text", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "text", "text": "claude_code.api_request_body"}},
                {"id": "part-bool-false", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "tool", "callID": "call-1", "tool": "read", "state": {"status": "completed", "input": {"path": "f"}, "output": false}}},
                {"id": "part-bool-true", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "tool", "callID": "call-2", "tool": "read", "state": {"status": "completed", "input": {"path": "f"}, "output": true}}},
                {"id": "part-number-int", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "tool", "callID": "call-3", "tool": "read", "state": {"status": "completed", "input": {"path": "f"}, "output": 46}}},
                {"id": "part-number-float", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "tool", "callID": "call-4", "tool": "read", "state": {"status": "completed", "input": {"path": "f"}, "output": 0.0125}}},
                {"id": "part-number-zero", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "tool", "callID": "call-5", "tool": "read", "state": {"status": "completed", "input": {"path": "f"}, "output": 0}}},
                {"id": "part-object", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "tool", "callID": "call-6", "tool": "read", "state": {"status": "completed", "input": {"path": "f"}, "output": {"bash_command": "git"}}}},
                {"id": "part-array", "message_id": "msg-a1", "time_created": __TS__, "data": {"type": "tool", "callID": "call-7", "tool": "read", "state": {"status": "completed", "input": {"path": "f"}, "output": ["a", "b"]}}}
              ]
            }
            """.Replace("__TS__", tick.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void NonStringToolOutputsStillReadAsText()
    {
        InMemoryRelationalStore store = RoundTrip();

        List<string?> texts =
        [
            .. store.FindSessionParts(SessionKey).Select(static part => part.PlainText),
        ];

        Assert.Contains("claude_code.api_request_body", texts, StringComparer.Ordinal);
        Assert.Contains("false", texts, StringComparer.Ordinal);
        Assert.Contains("true", texts, StringComparer.Ordinal);
        Assert.Contains("46", texts, StringComparer.Ordinal);
        Assert.Contains("0.0125", texts, StringComparer.Ordinal);
        Assert.Contains("0", texts, StringComparer.Ordinal);
    }

    [Fact]
    public void NestedToolOutputsAreWithheldInsteadOfFailingTheListing()
    {
        InMemoryRelationalStore store = RoundTrip();

        IReadOnlyList<CollectedContentPart> parts = store.FindSessionParts(SessionKey);
        IReadOnlyList<CollectedContentPart> withheld =
        [
            .. parts.Where(static part => part.Withheld is not null),
        ];

        // The relational store keeps JSON documents as withheld metrics rather than raw text.
        Assert.Equal(2, withheld.Count);
        Assert.All(withheld, static part => Assert.Null(part.PlainText));
        Assert.All(withheld, static part => Assert.True(part.Withheld!.Utf8Bytes > 0));
        Assert.All(withheld, static part => Assert.True(part.Withheld!.Characters > 0));

        // The listing itself answers every row instead of throwing over the mixed content.
        Assert.NotEmpty(store.SessionRecordsForRebuild(SessionKey));
        Assert.Equal(parts.Count, store.FindSessionParts(SessionKey).Count);
    }

    private static InMemoryRelationalStore RoundTrip()
    {
        string export = BuildExport();
        SourceUnit sourceUnit = new(
            HarnessId: KiloMapper.KiloSharedRuntimeHarnessId,
            SourceKey: "/fake-home/.local/share/kilo/kilo.db",
            ProviderId: "kilo",
            ParserId: "kilo-sqlite-reader",
            ParserContractVersion: "1",
            Format: "kilo-sqlite",
            SourceRootLabel: "kilo",
            RevisionToken: "1-1",
            LastWriteUtc: ImportedAtUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(export));
        NativeSourceUnit unit = new(
            Unit: sourceUnit,
            RawText: export,
            SizeBytes: Encoding.UTF8.GetByteCount(export),
            LastWriteUtc: ImportedAtUtc,
            CompanionText: null);

        CollectedBatch batch = new KiloMapper().Map(unit, ImportedAtUtc);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        return store;
    }
}
