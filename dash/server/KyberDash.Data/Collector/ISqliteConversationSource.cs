using System.Text.Json;

namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// What a harness-specific SQLite reader supplies to <see cref="SqliteHarnessSourceReader"/>: which
/// tables and columns it needs, which conversations a database holds, and how one conversation is
/// written as JSON. The generic reader owns opening, schema checks, caps and problem recording.
/// </summary>
public interface ISqliteConversationSource
{
    /// <summary>Tables and the columns the source reads from each; a missing one refuses the database.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<string>> RequiredColumns { get; }

    /// <summary>Lists the conversations in <paramref name="context"/>'s database.</summary>
    IEnumerable<SqliteConversation> ListConversations(SqliteConversationContext context);

    /// <summary>Writes the export of conversation <paramref name="conversationId"/> as one JSON value.</summary>
    /// <exception cref="InvalidDataException">The conversation no longer exists.</exception>
    void WriteExport(IHarnessSqliteDatabase database, string conversationId, Utf8JsonWriter writer);
}

/// <summary>Everything <see cref="ISqliteConversationSource.ListConversations"/> may need for one database.</summary>
/// <param name="Database">The open database.</param>
/// <param name="DatabasePath">Full path of the database file.</param>
/// <param name="HarnessId">Harness that owns the database.</param>
/// <param name="SinceUtc">Optional inclusive cutoff; older conversations may be skipped.</param>
/// <param name="HomeDirectory">User home directory.</param>
/// <param name="FileSystem">Filesystem seam for sibling files.</param>
/// <param name="Sqlite">Opener for a further database (for example a workspace database).</param>
/// <param name="Problems">Sink for recorded discovery problems.</param>
public sealed record SqliteConversationContext(
    IHarnessSqliteDatabase Database,
    string DatabasePath,
    string HarnessId,
    DateTimeOffset? SinceUtc,
    string HomeDirectory,
    IHarnessFileSystem FileSystem,
    IHarnessSqliteSource Sqlite,
    ICollection<CollectedProblem> Problems);

/// <summary>One conversation a database holds.</summary>
/// <param name="Id">Conversation identifier within the database.</param>
/// <param name="RevisionToken">Fingerprint that changes when the conversation changes.</param>
/// <param name="LastActivityUtc">Optional UTC time of the latest activity.</param>
/// <param name="CompanionKey">Optional companion file locator.</param>
public sealed record SqliteConversation(
    string Id,
    string RevisionToken,
    DateTimeOffset? LastActivityUtc,
    string? CompanionKey);
