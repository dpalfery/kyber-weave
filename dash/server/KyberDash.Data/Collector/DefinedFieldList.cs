using System.Collections.ObjectModel;
using KyberDash.Domain.Schema;

namespace KyberDash.Data.Collector;

/// <summary>
/// Closed vocabulary of defined collector fields mapped to <see cref="RelationalSchemaCatalog"/>
/// tables and columns.
/// </summary>
public enum DefinedFieldId
{
    /// <summary>Sentinel unset value; rejected by <see cref="DefinedFieldList"/>.</summary>
    None = 0,

    /// <summary>1. Invoked skill name (<c>record_skill_invocation.skill_name</c>).</summary>
    SkillName = 1,

    /// <summary>2. Tool input byte/character size (<c>tool_call_input_size(utf8_bytes, characters)</c>).</summary>
    ToolInputSize = 2,

    /// <summary>3. Tool input canonical SHA-256 fingerprint (<c>tool_call_input_fingerprint.sha256</c>).</summary>
    ToolInputFingerprint = 3,

    /// <summary>4. Tool input plain text (<c>tool_call_input_text.text</c>).</summary>
    ToolInputText = 4,

    /// <summary>5. Tool result byte/character size (<c>tool_call_result_size(utf8_bytes, characters)</c>).</summary>
    ToolResultSize = 5,

    /// <summary>6. Tool result plain text and truncation metadata (<c>tool_call_result_text.text</c>, <c>tool_call_result_truncation.original_utf8_bytes</c>).</summary>
    ToolResultText = 6,

    /// <summary>7. Tool exit code (<c>tool_call_exit_code.exit_code</c>).</summary>
    ToolExitCode = 7,

    /// <summary>8. Tool definitions and server attribution (<c>tool_definition</c>, <c>tool_definition_server.server_name</c>).</summary>
    ToolDefinitions = 8,

    /// <summary>9. Withheld JSON content part metrics (<c>content_part_withheld(utf8_bytes, characters, measured_tokens)</c>).</summary>
    ContentPartWithheld = 9,

    /// <summary>10. Content part reported tokens, server, role, residency, and truncation metadata.</summary>
    ContentPartMetadata = 10,

    /// <summary>11. Record error message (<c>record_error.message</c>).</summary>
    RecordError = 11,

    /// <summary>12. Record edit failure count (<c>record_edit_failure.failure_count</c>).</summary>
    EditFailures = 12,

    /// <summary>13. User correction signal and matched rule (<c>record_correction</c>, <c>record_correction_rule.correction_rule</c>).</summary>
    Correction = 13,

    /// <summary>14. User message content part (<c>content_part</c> with bucket <c>user_message</c>).</summary>
    UserMessage = 14,

    /// <summary>15. Declared context window (<c>record_declared_context_window.declared_context_window</c>).</summary>
    DeclaredContextWindow = 15,

    /// <summary>Reported input/output token totals (<c>record_reported_total(direction, token_count)</c>).</summary>
    ReportedTotals = 16,
}

/// <summary>
/// A single normalized table/column target in <see cref="RelationalSchemaCatalog"/>.
/// </summary>
/// <param name="TableName">Normalized table name.</param>
/// <param name="ColumnName">Normalized column name.</param>
public record DefinedFieldColumn(string TableName, string ColumnName);

/// <summary>
/// One entry in the closed <see cref="DefinedFieldList"/> mapping to <see cref="RelationalSchemaCatalog"/>.
/// </summary>
/// <param name="Id">Closed field identifier.</param>
/// <param name="Number">Ordinal number (1..15) for the numbered defined-field entries, or <see langword="null"/> for <see cref="DefinedFieldId.ReportedTotals"/>.</param>
/// <param name="Name">Human-readable entry name.</param>
/// <param name="Columns">Normalized catalog table/column targets.</param>
/// <param name="RequiredBucketLiteral">Optional required <c>content_part.bucket</c> enum literal.</param>
public record DefinedFieldEntry(
    DefinedFieldId Id,
    int? Number,
    string Name,
    IReadOnlyList<DefinedFieldColumn> Columns,
    string? RequiredBucketLiteral = null);

/// <summary>
/// Enforces the closed vocabulary of 15 defined collector fields plus reported totals,
/// verified against <see cref="RelationalSchemaCatalog"/>.
/// </summary>
public static class DefinedFieldList
{
    private static readonly IReadOnlyList<DefinedFieldEntry> NumberedList = BuildNumberedEntries();

    private static readonly DefinedFieldEntry ReportedTotalsEntry = new(
        DefinedFieldId.ReportedTotals,
        Number: null,
        Name: "Reported totals",
        Columns:
        [
            new DefinedFieldColumn("record_reported_total", "direction"),
            new DefinedFieldColumn("record_reported_total", "token_count"),
        ]);

    private static readonly IReadOnlyList<DefinedFieldEntry> AllEntries = BuildAllEntries();

    private static readonly IReadOnlyDictionary<DefinedFieldId, DefinedFieldEntry> EntriesById =
        new ReadOnlyDictionary<DefinedFieldId, DefinedFieldEntry>(
            AllEntries.ToDictionary(static entry => entry.Id));

    static DefinedFieldList()
    {
        VerifyAgainstCatalog();
    }

    /// <summary>The exact 15 numbered defined-field entries.</summary>
    public static IReadOnlyList<DefinedFieldEntry> NumberedEntries => NumberedList;

    /// <summary>The reported-totals entry (<c>record_reported_total(direction, token_count)</c>).</summary>
    public static DefinedFieldEntry ReportedTotals => ReportedTotalsEntry;

    /// <summary>All closed entries (the 15 numbered entries plus <see cref="ReportedTotals"/>).</summary>
    public static IReadOnlyList<DefinedFieldEntry> Entries => AllEntries;

    /// <summary>Returns whether <paramref name="id"/> belongs to the closed field vocabulary.</summary>
    /// <param name="id">Field identifier to check.</param>
    /// <returns><see langword="true"/> when defined; otherwise <see langword="false"/>.</returns>
    public static bool IsDefined(DefinedFieldId id) => EntriesById.ContainsKey(id);

    /// <summary>Resolves the <see cref="DefinedFieldEntry"/> for <paramref name="id"/>, failing closed on unknown values.</summary>
    /// <param name="id">Closed field identifier.</param>
    /// <returns>The matching <see cref="DefinedFieldEntry"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="id"/> is not part of the closed vocabulary.</exception>
    public static DefinedFieldEntry Get(DefinedFieldId id)
    {
        if (EntriesById.TryGetValue(id, out DefinedFieldEntry? entry))
            return entry;

        throw new ArgumentOutOfRangeException(
            nameof(id),
            id,
            $"Unknown defined field id '{id}'. Only the closed 15 defined fields and ReportedTotals are permitted.");
    }

    /// <summary>
    /// Verifies that the closed field list has exact cardinality and that every target table,
    /// column, and required bucket literal exists in <see cref="RelationalSchemaCatalog"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if any entry violates catalog invariants.</exception>
    public static void VerifyAgainstCatalog()
    {
        if (NumberedList.Count != 15)
            throw new InvalidOperationException($"Expected exactly 15 numbered defined fields, found {NumberedList.Count}.");

        if (AllEntries.Count != 16)
            throw new InvalidOperationException($"Expected 16 total defined field entries, found {AllEntries.Count}.");

        for (int i = 0; i < NumberedList.Count; i++)
        {
            if (NumberedList[i].Number != i + 1)
            {
                throw new InvalidOperationException(
                    $"Defined field '{NumberedList[i].Id}' has ordinal {NumberedList[i].Number}, expected {i + 1}.");
            }
        }

        foreach (DefinedFieldEntry entry in AllEntries)
        {
            if (entry.Columns.Count == 0)
                throw new InvalidOperationException($"Defined field '{entry.Id}' must declare at least one catalog column.");

            foreach (DefinedFieldColumn column in entry.Columns)
            {
                TableDefinition table = RelationalSchemaCatalog.GetTable(column.TableName);
                ColumnDefinition? matched = table.Columns.FirstOrDefault(
                    c => string.Equals(c.Name, column.ColumnName, StringComparison.Ordinal));
                if (matched is null)
                {
                    throw new InvalidOperationException(
                        $"Defined field '{entry.Id}' references unknown column '{column.TableName}.{column.ColumnName}'.");
                }

                if (entry.RequiredBucketLiteral is not null)
                {
                    string expectedLiteral = $"'{entry.RequiredBucketLiteral}'";
                    if (matched.CheckConstraint is null || !matched.CheckConstraint.Contains(expectedLiteral, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Defined field '{entry.Id}' requires bucket literal {expectedLiteral} on '{column.TableName}.{column.ColumnName}'.");
                    }
                }
            }
        }
    }

    private static IReadOnlyList<DefinedFieldEntry> BuildNumberedEntries() =>
    [
        new DefinedFieldEntry(
            DefinedFieldId.SkillName,
            Number: 1,
            Name: "Skill name",
            Columns: [new DefinedFieldColumn("record_skill_invocation", "skill_name")]),
        new DefinedFieldEntry(
            DefinedFieldId.ToolInputSize,
            Number: 2,
            Name: "Tool input size",
            Columns:
            [
                new DefinedFieldColumn("tool_call_input_size", "utf8_bytes"),
                new DefinedFieldColumn("tool_call_input_size", "characters"),
            ]),
        new DefinedFieldEntry(
            DefinedFieldId.ToolInputFingerprint,
            Number: 3,
            Name: "Tool input fingerprint",
            Columns: [new DefinedFieldColumn("tool_call_input_fingerprint", "sha256")]),
        new DefinedFieldEntry(
            DefinedFieldId.ToolInputText,
            Number: 4,
            Name: "Tool input text",
            Columns: [new DefinedFieldColumn("tool_call_input_text", "text")]),
        new DefinedFieldEntry(
            DefinedFieldId.ToolResultSize,
            Number: 5,
            Name: "Tool result size",
            Columns:
            [
                new DefinedFieldColumn("tool_call_result_size", "utf8_bytes"),
                new DefinedFieldColumn("tool_call_result_size", "characters"),
            ]),
        new DefinedFieldEntry(
            DefinedFieldId.ToolResultText,
            Number: 6,
            Name: "Tool result text",
            Columns:
            [
                new DefinedFieldColumn("tool_call_result_text", "text"),
                new DefinedFieldColumn("tool_call_result_truncation", "original_utf8_bytes"),
            ]),
        new DefinedFieldEntry(
            DefinedFieldId.ToolExitCode,
            Number: 7,
            Name: "Tool exit code",
            Columns: [new DefinedFieldColumn("tool_call_exit_code", "exit_code")]),
        new DefinedFieldEntry(
            DefinedFieldId.ToolDefinitions,
            Number: 8,
            Name: "Tool definitions",
            Columns:
            [
                new DefinedFieldColumn("tool_definition", "position"),
                new DefinedFieldColumn("tool_definition", "tool_name"),
                new DefinedFieldColumn("tool_definition", "schema_tokens"),
                new DefinedFieldColumn("tool_definition_server", "server_name"),
            ]),
        new DefinedFieldEntry(
            DefinedFieldId.ContentPartWithheld,
            Number: 9,
            Name: "Content part JSON withheld",
            Columns:
            [
                new DefinedFieldColumn("content_part_withheld", "utf8_bytes"),
                new DefinedFieldColumn("content_part_withheld", "characters"),
                new DefinedFieldColumn("content_part_withheld", "measured_tokens"),
            ]),
        new DefinedFieldEntry(
            DefinedFieldId.ContentPartMetadata,
            Number: 10,
            Name: "Content part reported tokens/server/role/residency/truncation",
            Columns:
            [
                new DefinedFieldColumn("content_part_reported_tokens", "token_count"),
                new DefinedFieldColumn("content_part_server", "server_name"),
                new DefinedFieldColumn("content_part_role", "role_name"),
                new DefinedFieldColumn("content_part_residency", "is_resident"),
                new DefinedFieldColumn("content_part_truncation", "original_utf8_bytes"),
            ]),
        new DefinedFieldEntry(
            DefinedFieldId.RecordError,
            Number: 11,
            Name: "Record error",
            Columns: [new DefinedFieldColumn("record_error", "message")]),
        new DefinedFieldEntry(
            DefinedFieldId.EditFailures,
            Number: 12,
            Name: "Edit failures",
            Columns: [new DefinedFieldColumn("record_edit_failure", "failure_count")]),
        new DefinedFieldEntry(
            DefinedFieldId.Correction,
            Number: 13,
            Name: "Correction",
            Columns:
            [
                new DefinedFieldColumn("record_correction", "span_id"),
                new DefinedFieldColumn("record_correction_rule", "correction_rule"),
            ]),
        new DefinedFieldEntry(
            DefinedFieldId.UserMessage,
            Number: 14,
            Name: "User message",
            Columns: [new DefinedFieldColumn("content_part", "bucket")],
            RequiredBucketLiteral: "user_message"),
        new DefinedFieldEntry(
            DefinedFieldId.DeclaredContextWindow,
            Number: 15,
            Name: "Declared context window",
            Columns: [new DefinedFieldColumn("record_declared_context_window", "declared_context_window")]),
    ];

    private static IReadOnlyList<DefinedFieldEntry> BuildAllEntries() =>
        [.. NumberedList, ReportedTotalsEntry];
}
