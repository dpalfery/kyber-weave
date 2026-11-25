using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KyberDash.Data.Collector.Sources;

/// <summary>
/// Generic <see cref="IHarnessSourceReader"/> for harnesses that keep conversations in a SQLite database.
/// It lists one <see cref="SourceUnit"/> per conversation and reads each into a capped JSON export; the
/// harness-specific part is an <see cref="ISqliteConversationSource"/>.
/// </summary>
/// <remarks>
/// A unit per conversation (not per database) keeps an unchanged conversation's checkpoint valid and keeps
/// each export small. Every failure is recorded rather than swallowed: discovery failures become
/// <see cref="CollectedProblem"/> rows, and read failures throw <see cref="IOException"/> or
/// <see cref="InvalidDataException"/> whose message starts with the problem code, which the ingest path
/// records against the unit. A missing table or column refuses the whole database (fail closed) instead of
/// reading half of it.
/// </remarks>
public class SqliteHarnessSourceReader : IHarnessSourceReader
{
    private const char KeySeparator = '#';

    private static readonly string[] KnownCodes =
    [
        SqliteSourceOpenPolicy.SourceUnreadableCode,
        SqliteSourceOpenPolicy.SchemaMissingCode,
        SqliteSourceOpenPolicy.ExportCapExceededCode,
        SqliteSourceOpenPolicy.CopyCapExceededCode,
    ];

    private readonly string providerId;
    private readonly string parserId;
    private readonly string format;
    private readonly string parserContractVersion;
    private readonly IReadOnlyList<string> explicitRelativeDatabasePaths;
    private readonly ISqliteConversationSource conversations;
    private readonly IHarnessSqliteSource sqlite;
    private readonly HarnessSqliteLimits limits;

    /// <summary>Creates a reader for <paramref name="harnessId"/>'s databases.</summary>
    /// <param name="harnessId">Canonical harness identifier.</param>
    /// <param name="providerId">Upstream provider identifier.</param>
    /// <param name="parserId">Parser identifier.</param>
    /// <param name="format">Native source format identifier.</param>
    /// <param name="parserContractVersion">Parser contract version.</param>
    /// <param name="explicitRelativeDatabasePaths">Database paths relative to the home directory.</param>
    /// <param name="conversations">The harness-specific conversation source.</param>
    /// <param name="sqlite">Read-only SQLite opener.</param>
    /// <param name="limits">Export and copy limits.</param>
    public SqliteHarnessSourceReader(
        string harnessId,
        string providerId,
        string parserId,
        string format,
        string parserContractVersion,
        IReadOnlyList<string> explicitRelativeDatabasePaths,
        ISqliteConversationSource conversations,
        IHarnessSqliteSource sqlite,
        HarnessSqliteLimits limits)
    {
        HarnessId = harnessId;
        this.providerId = providerId;
        this.parserId = parserId;
        this.format = format;
        this.parserContractVersion = parserContractVersion;
        this.explicitRelativeDatabasePaths = explicitRelativeDatabasePaths;
        this.conversations = conversations;
        this.sqlite = sqlite;
        this.limits = limits;
    }

    /// <inheritdoc />
    public string HarnessId { get; }

    /// <inheritdoc />
    public bool IsOtlpOnly => false;

    /// <summary>Writes <paramref name="value"/> under <paramref name="propertyName"/> as the JSON it represents.</summary>
    /// <remarks>
    /// Text that parses as a JSON object or array is written raw, other text as a string, integers and reals
    /// as numbers, and NULL as <c>null</c>. A non-finite real has no JSON form and is written as <c>null</c>.
    /// </remarks>
    public static void WriteJsonColumn(Utf8JsonWriter writer, string propertyName, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value)
        {
            case null:
                writer.WriteNull(propertyName);
                break;
            case string text:
                writer.WritePropertyName(propertyName);
                WriteTextValue(writer, text);
                break;
            case long or int or short or byte or sbyte or ushort or uint or ulong:
                writer.WriteNumber(propertyName, Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                break;
            case double or float or decimal:
                double real = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(real))
                {
                    writer.WriteNumber(propertyName, real);
                }
                else
                {
                    writer.WriteNull(propertyName);
                }

                break;
            case byte[] bytes:
                writer.WriteBase64String(propertyName, bytes);
                break;
            default:
                writer.WriteString(propertyName, Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <inheritdoc />
    public SourceDiscoveryResult Discover(SourceDiscoveryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<SourceUnit> units = [];
        List<CollectedProblem> problems = [];

        foreach (string relativePath in explicitRelativeDatabasePaths)
        {
            string databasePath = context.FileSystem.GetFullPath(Path.Combine(context.HomeDirectory, relativePath));
            if (!context.FileSystem.FileExists(databasePath))
            {
                continue;
            }

            DiscoverDatabase(databasePath, context, units, problems);
        }

        return new SourceDiscoveryResult(units, problems);
    }

    /// <summary>Splits a unit's source key (<c>&lt;database path&gt;#&lt;conversation id&gt;</c>) at its last separator.</summary>
    /// <param name="sourceKey">The unit's source key.</param>
    /// <returns>The database path and conversation id, or <see langword="null"/> when the key has no separator.</returns>
    internal static (string DatabasePath, string ConversationId)? SplitSourceKey(string sourceKey)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        int separator = sourceKey.LastIndexOf(KeySeparator);
        return separator < 0 ? null : (sourceKey[..separator], sourceKey[(separator + 1)..]);
    }

    /// <inheritdoc />
    public NativeSourceUnit? Read(SourceUnit unit, IHarnessFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(fileSystem);
        (string databasePath, string conversationId) = SplitSourceKey(unit.SourceKey)
            ?? throw new InvalidDataException(
                $"{SqliteSourceOpenPolicy.SourceUnreadableCode}: source key '{unit.SourceKey}' has no '{KeySeparator}' conversation separator.");

        string exportText;
        long exportBytes;
        using (IHarnessSqliteDatabase database = sqlite.Open(databasePath))
        using (BoundedMemoryStream stream = new(limits.ExportCapBytes))
        {
            using (Utf8JsonWriter writer = new(stream))
            {
                conversations.WriteExport(database, conversationId, writer);
                writer.Flush();
            }

            exportBytes = stream.Length;
            exportText = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }

        string? companionText = unit.CompanionKey is not null && fileSystem.FileExists(unit.CompanionKey)
            ? fileSystem.ReadAllText(unit.CompanionKey)
            : null;

        return new NativeSourceUnit(
            unit,
            exportText,
            exportBytes,
            unit.LastWriteUtc ?? DateTimeOffset.UnixEpoch,
            companionText);
    }

    private static void WriteTextValue(Utf8JsonWriter writer, string text)
    {
        if (LooksLikeJsonContainer(text) && IsValidJson(text))
        {
            writer.WriteRawValue(text.Trim(), skipInputValidation: true);
        }
        else
        {
            writer.WriteStringValue(text);
        }
    }

    private static bool LooksLikeJsonContainer(string text)
    {
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            return character is '{' or '[';
        }

        return false;
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CollectedProblem SourceProblem(string code, string harnessId, string databasePath, string message) =>
        new(
            ProblemKey: $"sqlite-source:{code}:{harnessId}:{databasePath}",
            Severity: "warning",
            Code: code,
            Message: message,
            Location: databasePath,
            HarnessId: harnessId);

    private static string CodeOf(string message)
    {
        foreach (string code in KnownCodes)
        {
            if (message.StartsWith(code, StringComparison.Ordinal))
            {
                return code;
            }
        }

        return SqliteSourceOpenPolicy.SourceUnreadableCode;
    }

    private void DiscoverDatabase(
        string databasePath,
        SourceDiscoveryContext context,
        List<SourceUnit> units,
        List<CollectedProblem> problems)
    {
        try
        {
            using IHarnessSqliteDatabase database = sqlite.Open(databasePath);
            string? missing = FindMissingSchema(database);
            if (missing is not null)
            {
                problems.Add(SourceProblem(
                    SqliteSourceOpenPolicy.SchemaMissingCode,
                    HarnessId,
                    databasePath,
                    $"{SqliteSourceOpenPolicy.SchemaMissingCode}: '{databasePath}' lacks {missing}."));
                return;
            }

            // Materialize inside the try: a lazy enumerator can fail while it is being walked.
            List<SqliteConversation> listed = [.. conversations.ListConversations(new SqliteConversationContext(
                database,
                databasePath,
                HarnessId,
                context.SinceUtc,
                context.HomeDirectory,
                context.FileSystem,
                sqlite,
                problems))];
            string label = Path.GetFileName(databasePath);
            foreach (SqliteConversation conversation in listed)
            {
                units.Add(new SourceUnit(
                    HarnessId,
                    $"{databasePath}{KeySeparator}{conversation.Id}",
                    providerId,
                    parserId,
                    parserContractVersion,
                    format,
                    label,
                    conversation.RevisionToken,
                    conversation.LastActivityUtc,
                    CompanionKey: conversation.CompanionKey,
                    Kind: SourceKind.File));
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            problems.Add(SourceProblem(CodeOf(exception.Message), HarnessId, databasePath, exception.Message));
        }
    }

    private string? FindMissingSchema(IHarnessSqliteDatabase database)
    {
        List<string> missing = [];
        foreach ((string table, IReadOnlyList<string> columns) in conversations.RequiredColumns)
        {
            IReadOnlySet<string> present = database.GetColumns(table);
            if (present.Count == 0)
            {
                missing.Add($"table '{table}'");
                continue;
            }

            foreach (string column in columns)
            {
                if (!present.Contains(column))
                {
                    missing.Add($"column '{table}.{column}'");
                }
            }
        }

        return missing.Count == 0 ? null : string.Join(", ", missing);
    }

    /// <summary>A growable memory stream that refuses to hold more than its cap.</summary>
    private sealed class BoundedMemoryStream(long capBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureWithinCap(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureWithinCap(buffer.Length);
            base.Write(buffer);
        }

        private void EnsureWithinCap(int incoming)
        {
            if (Position + incoming > capBytes)
            {
                throw new InvalidDataException(
                    $"{SqliteSourceOpenPolicy.ExportCapExceededCode}: export is larger than {capBytes} bytes.");
            }
        }
    }
}
