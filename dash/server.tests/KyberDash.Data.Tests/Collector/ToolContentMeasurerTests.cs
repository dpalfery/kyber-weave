using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Unit tests for <see cref="JsonDocumentText"/>, <see cref="ToolContentMeasurer"/>, and
/// <see cref="DefinedFieldList"/>.
/// </summary>
public class ToolContentMeasurerTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"a\":1}")]
    [InlineData("  \n\t{ \"nested\": { \"b\": [1, true, null, \"ok\"] } } \r\n")]
    [InlineData("  [1, \"two\", {\"three\": 3}]  ")]
    public void IsDocument_ReturnsTrue_ForValidJsonObjectOrArray(string input)
    {
        Assert.True(JsonDocumentText.IsDocument(input));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("-3.14")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("\"{\\\"a\\\":1}\"")]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    [InlineData("Plain prose mentioning {\"a\":1} inline")]
    [InlineData("Prefix [1, 2, 3]")]
    [InlineData("{\"a\":1} trailing")]
    [InlineData("{\"a\":1}{\"b\":2}")]
    [InlineData("[1,2] extra")]
    [InlineData("{not json}")]
    [InlineData("[1, 2,")]
    [InlineData("{\"a\":1,}")]
    public void IsDocument_ReturnsFalse_ForScalarsBlankProseAndMalformedOrTrailingJson(string input)
    {
        Assert.False(JsonDocumentText.IsDocument(input));
    }

    [Fact]
    public void IsDocument_ReturnsFalse_ForNull()
    {
        Assert.False(JsonDocumentText.IsDocument(null));
    }

    [Fact]
    public void Measure_WithholdsJsonObjectAndComputesCanonicalSortedMinifiedSha256()
    {
        string unformatted = """
            {
              "z": 1,
              "a": {
                "d": [3, 2, 1],
                "b": "hello"
              }
            }
            """;
        string equivalentReordered = "{\"a\":{\"b\":\"hello\",\"d\":[3,2,1]},\"z\":1}";

        ToolContentMeasurement first = ToolContentMeasurer.Measure(unformatted);
        ToolContentMeasurement second = ToolContentMeasurer.Measure(equivalentReordered);

        Assert.True(first.IsDocument);
        Assert.Null(first.PlainText);
        Assert.Null(first.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(unformatted), first.Utf8Bytes);
        Assert.Equal(unformatted.Length, first.Characters);
        Assert.Equal(CanonTokenCache.ApproximateO200kBase(unformatted), first.MeasuredTokens);

        string expectedSha256 = Sha256Hex(equivalentReordered);
        Assert.Equal(expectedSha256, first.Sha256Fingerprint);
        Assert.Equal(first.Sha256Fingerprint, second.Sha256Fingerprint);

        CollectedToolContent toolContent = first.ToToolContent();
        Assert.Null(toolContent.PlainText);
        Assert.Equal(first.Utf8Bytes, toolContent.Utf8Bytes);
        Assert.Equal(first.Characters, toolContent.Characters);
        Assert.Equal(expectedSha256, toolContent.Sha256Fingerprint);
        Assert.Null(toolContent.OriginalUtf8BytesWhenTruncated);

        CollectedWithheldText withheld = first.ToWithheldText();
        Assert.Equal(first.Utf8Bytes, withheld.Utf8Bytes);
        Assert.Equal(first.Characters, withheld.Characters);
        Assert.Equal(first.MeasuredTokens, withheld.MeasuredTokens);
    }

    [Fact]
    public void Measure_WithholdsJsonArrayAndPreservesArrayOrderInSha256()
    {
        string arrayForward = "[{\"b\":2,\"a\":1},{\"d\":4,\"c\":3}]";
        string arrayReversed = "[{\"c\":3,\"d\":4},{\"a\":1,\"b\":2}]";

        ToolContentMeasurement forward = ToolContentMeasurer.Measure(arrayForward);
        ToolContentMeasurement reversed = ToolContentMeasurer.Measure(arrayReversed);

        Assert.True(forward.IsDocument);
        Assert.Null(forward.PlainText);
        Assert.Equal(Sha256Hex("[{\"a\":1,\"b\":2},{\"c\":3,\"d\":4}]"), forward.Sha256Fingerprint);
        Assert.NotEqual(forward.Sha256Fingerprint, reversed.Sha256Fingerprint);
    }

    [Theory]
    [InlineData("\"{\\\"b\\\":1,\\\"a\\\":2}\"", "{\"a\":2,\"b\":1}")]
    [InlineData("\"  [ 10 , 20 ]  \"", "[10,20]")]
    [InlineData("\"\\\"{\\\\\\\"b\\\\\\\":1,\\\\\\\"a\\\\\\\":2}\\\"\"", "{\"a\":2,\"b\":1}")]
    public void Measure_WithholdsNestedJsonStringThatParsesAsObjectOrArray(string nestedJsonString, string expectedCanonicalJson)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(nestedJsonString);

        Assert.True(measurement.IsDocument);
        Assert.Null(measurement.PlainText);
        Assert.Null(measurement.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(nestedJsonString), measurement.Utf8Bytes);
        Assert.Equal(nestedJsonString.Length, measurement.Characters);
        Assert.Equal(CanonTokenCache.ApproximateO200kBase(nestedJsonString), measurement.MeasuredTokens);
        Assert.Equal(Sha256Hex(expectedCanonicalJson), measurement.Sha256Fingerprint);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"x\"")]
    [InlineData("Here is a JSON snippet: {\"a\":1}")]
    public void Measure_PreservesJsonScalarsAndProseAsPlainText(string input)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(input);

        Assert.False(measurement.IsDocument);
        Assert.Equal(input, measurement.PlainText);
        Assert.Null(measurement.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), measurement.Utf8Bytes);
        Assert.Equal(input.Length, measurement.Characters);
        Assert.Equal(CanonTokenCache.ApproximateO200kBase(input), measurement.MeasuredTokens);
        Assert.Equal(Sha256Hex(input), measurement.Sha256Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \t ")]
    public void Measure_PreservesEmptyAndBlankInput(string input)
    {
        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(input);

        Assert.False(measurement.IsDocument);
        Assert.Equal(input, measurement.PlainText);
        Assert.Null(measurement.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(input), measurement.Utf8Bytes);
        Assert.Equal(input.Length, measurement.Characters);
        Assert.Equal(CanonTokenCache.ApproximateO200kBase(input), measurement.MeasuredTokens);
        Assert.Equal(Sha256Hex(input), measurement.Sha256Fingerprint);
    }

    [Fact]
    public void Measure_TruncatesOversizedPlainTextAndRecordsOriginalUtf8Bytes()
    {
        string prefix = new('x', CanonSynth.MaxToolResultBytes - 2);
        string oversized = prefix + "😀tail";
        int originalBytes = Encoding.UTF8.GetByteCount(oversized);
        Assert.True(originalBytes > CanonSynth.MaxToolResultBytes);

        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(oversized);

        Assert.False(measurement.IsDocument);
        Assert.NotNull(measurement.PlainText);
        Assert.Equal(CanonValidation.TruncateContentPartUtf8(oversized, CanonSynth.MaxToolResultBytes), measurement.PlainText);
        Assert.True(Encoding.UTF8.GetByteCount(measurement.PlainText!) <= CanonSynth.MaxToolResultBytes);
        Assert.Equal(originalBytes, measurement.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(originalBytes, measurement.Utf8Bytes);
        Assert.Equal(oversized.Length, measurement.Characters);
        Assert.Equal(CanonTokenCache.ApproximateO200kBase(oversized), measurement.MeasuredTokens);
        Assert.Equal(Sha256Hex(oversized), measurement.Sha256Fingerprint);
    }

    [Fact]
    public void Measure_DoesNotRecordTruncation_WhenOversizedJsonDocumentIsWithheld()
    {
        string largePayload = "{\"payload\":\"" + new string('a', CanonSynth.MaxToolResultBytes + 100) + "\"}";

        ToolContentMeasurement measurement = ToolContentMeasurer.Measure(largePayload);

        Assert.True(measurement.IsDocument);
        Assert.Null(measurement.PlainText);
        Assert.Null(measurement.OriginalUtf8BytesWhenTruncated);
        Assert.True(measurement.Utf8Bytes > CanonSynth.MaxToolResultBytes);
        Assert.Equal(Sha256Hex(largePayload), measurement.Sha256Fingerprint);
    }

    [Fact]
    public void Measure_JsonElementOverload_HandlesDocumentsNestedStringsAndScalars()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"obj":{"b":1,"a":2},"nested":"{\"y\":2,\"x\":1}","plain":"hello","num":42}""");
        JsonElement root = doc.RootElement;

        ToolContentMeasurement objResult = ToolContentMeasurer.Measure(root.GetProperty("obj"));
        ToolContentMeasurement nestedResult = ToolContentMeasurer.Measure(root.GetProperty("nested"));
        ToolContentMeasurement plainResult = ToolContentMeasurer.Measure(root.GetProperty("plain"));
        ToolContentMeasurement numResult = ToolContentMeasurer.Measure(root.GetProperty("num"));

        Assert.True(objResult.IsDocument);
        Assert.Null(objResult.PlainText);
        Assert.Equal(Sha256Hex("{\"a\":2,\"b\":1}"), objResult.Sha256Fingerprint);

        Assert.True(nestedResult.IsDocument);
        Assert.Null(nestedResult.PlainText);
        Assert.Equal(Sha256Hex("{\"x\":1,\"y\":2}"), nestedResult.Sha256Fingerprint);

        Assert.False(plainResult.IsDocument);
        Assert.Equal("hello", plainResult.PlainText);

        Assert.False(numResult.IsDocument);
        Assert.Equal("42", numResult.PlainText);
    }

    [Fact]
    public void DefinedFieldList_EnforcesExactFifteenNumberedEntriesAndReportedTotals()
    {
        Assert.Equal(15, DefinedFieldList.NumberedEntries.Count);
        Assert.Equal(16, DefinedFieldList.Entries.Count);

        for (int i = 0; i < 15; i++)
        {
            Assert.Equal(i + 1, DefinedFieldList.NumberedEntries[i].Number);
        }

        Assert.Null(DefinedFieldList.ReportedTotals.Number);
        Assert.Equal(DefinedFieldId.ReportedTotals, DefinedFieldList.ReportedTotals.Id);

        AssertColumns(DefinedFieldId.SkillName, ("record_skill_invocation", "skill_name"));
        AssertColumns(DefinedFieldId.ToolInputSize, ("tool_call_input_size", "utf8_bytes"), ("tool_call_input_size", "characters"));
        AssertColumns(DefinedFieldId.ToolInputFingerprint, ("tool_call_input_fingerprint", "sha256"));
        AssertColumns(DefinedFieldId.ToolInputText, ("tool_call_input_text", "text"));
        AssertColumns(DefinedFieldId.ToolResultSize, ("tool_call_result_size", "utf8_bytes"), ("tool_call_result_size", "characters"));
        AssertColumns(DefinedFieldId.ToolResultText, ("tool_call_result_text", "text"), ("tool_call_result_truncation", "original_utf8_bytes"));
        AssertColumns(DefinedFieldId.ToolExitCode, ("tool_call_exit_code", "exit_code"));
        AssertColumns(
            DefinedFieldId.ToolDefinitions,
            ("tool_definition", "position"),
            ("tool_definition", "tool_name"),
            ("tool_definition", "schema_tokens"),
            ("tool_definition_server", "server_name"));
        AssertColumns(
            DefinedFieldId.ContentPartWithheld,
            ("content_part_withheld", "utf8_bytes"),
            ("content_part_withheld", "characters"),
            ("content_part_withheld", "measured_tokens"));
        AssertColumns(
            DefinedFieldId.ContentPartMetadata,
            ("content_part_reported_tokens", "token_count"),
            ("content_part_server", "server_name"),
            ("content_part_role", "role_name"),
            ("content_part_residency", "is_resident"),
            ("content_part_truncation", "original_utf8_bytes"));
        AssertColumns(DefinedFieldId.RecordError, ("record_error", "message"));
        AssertColumns(DefinedFieldId.EditFailures, ("record_edit_failure", "failure_count"));
        AssertColumns(DefinedFieldId.Correction, ("record_correction", "span_id"), ("record_correction_rule", "correction_rule"));
        AssertColumns(DefinedFieldId.UserMessage, ("content_part", "bucket"));
        Assert.Equal("user_message", DefinedFieldList.Get(DefinedFieldId.UserMessage).RequiredBucketLiteral);
        AssertColumns(DefinedFieldId.DeclaredContextWindow, ("record_declared_context_window", "declared_context_window"));
        AssertColumns(DefinedFieldId.ReportedTotals, ("record_reported_total", "direction"), ("record_reported_total", "token_count"));
    }

    [Fact]
    public void DefinedFieldList_VerifiesEveryEntryAgainstRelationalSchemaCatalog()
    {
        DefinedFieldList.VerifyAgainstCatalog();

        foreach (DefinedFieldEntry entry in DefinedFieldList.Entries)
        {
            Assert.NotEmpty(entry.Columns);
            foreach (DefinedFieldColumn column in entry.Columns)
            {
                TableDefinition table = RelationalSchemaCatalog.GetTable(column.TableName);
                ColumnDefinition? matched = table.Columns.FirstOrDefault(
                    c => string.Equals(c.Name, column.ColumnName, StringComparison.Ordinal));
                Assert.NotNull(matched);

                if (entry.RequiredBucketLiteral is not null)
                {
                    Assert.Contains($"'{entry.RequiredBucketLiteral}'", matched!.CheckConstraint ?? string.Empty, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void DefinedFieldList_RejectsUndefinedFieldIds()
    {
        DefinedFieldId invalid = (DefinedFieldId)999;

        Assert.False(DefinedFieldList.IsDefined(DefinedFieldId.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => DefinedFieldList.Get(DefinedFieldId.None));
        Assert.False(DefinedFieldList.IsDefined(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => DefinedFieldList.Get(invalid));
    }

    private static void AssertColumns(DefinedFieldId id, params (string TableName, string ColumnName)[] expected)
    {
        DefinedFieldEntry entry = DefinedFieldList.Get(id);
        Assert.Equal(expected.Length, entry.Columns.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].TableName, entry.Columns[i].TableName);
            Assert.Equal(expected[i].ColumnName, entry.Columns[i].ColumnName);
        }
    }

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
