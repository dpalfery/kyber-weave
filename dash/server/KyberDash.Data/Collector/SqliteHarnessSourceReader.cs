using System.Buffers;
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
        {
            // The writer commits into the capped buffer as it grows, so an export over the cap stops while it is
            // being written; a writer over a stream would hold the whole export in its own buffer until Flush.
            BoundedBufferWriter buffer = new(limits.ExportCapBytes);
            using (Utf8JsonWriter writer = new(buffer))
            {
                try
                {
                    conversations.WriteExport(database, conversationId, writer);
                    writer.Flush();
                }
                catch
                {
                    // Drop the pending bytes so Dispose does not flush them into the refusing buffer a second time.
                    writer.Reset();
                    throw;
                }
            }

            exportBytes = buffer.WrittenCount;
            exportText = Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        string? companionText = ReadCompanion(unit.CompanionKey, fileSystem);

        // default is the "not observed" value every mapper checks for; an invented date would be stamped on rows.
        return new NativeSourceUnit(
            unit,
            exportText,
            exportBytes,
            unit.LastWriteUtc ?? default,
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

    private string? ReadCompanion(string? companionKey, IHarnessFileSystem fileSystem)
    {
        if (companionKey is null || !fileSystem.FileExists(companionKey))
        {
            return null;
        }

        if (!fileSystem.TryGetFileStats(companionKey, out long lengthBytes, out _))
        {
            throw new InvalidDataException(
                $"{SqliteSourceOpenPolicy.SourceUnreadableCode}: companion file '{companionKey}' has no readable size, so it is not read.");
        }

        if (lengthBytes > limits.ExportCapBytes)
        {
            throw new InvalidDataException(
                $"{SqliteSourceOpenPolicy.ExportCapExceededCode}: companion file '{companionKey}' is {lengthBytes} bytes, larger than {limits.ExportCapBytes} bytes.");
        }

        return fileSystem.ReadAllText(companionKey);
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

    /// <summary>
    /// A growable buffer that refuses to commit more than its cap. The cap is checked on <see cref="Advance"/>,
    /// the only way bytes are committed; a size hint is a worst case (escaping can triple a string) and is not
    /// held against the cap.
    /// </summary>
    private sealed class BoundedBufferWriter(long capBytes) : IBufferWriter<byte>
    {
        private const int MinimumGrowth = 4096;

        private byte[] buffer = [];
        private int written;

        public int WrittenCount => written;

        public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, written);

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (written + (long)count > capBytes)
            {
                throw new InvalidDataException(
                    $"{SqliteSourceOpenPolicy.ExportCapExceededCode}: export is larger than {capBytes} bytes.");
            }

            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - written);
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Reserve(sizeHint);
            return buffer.AsMemory(written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Reserve(sizeHint);
            return buffer.AsSpan(written);
        }

        private void Reserve(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            int wanted = Math.Max(sizeHint, 1);
            if (buffer.Length - written >= wanted)
            {
                return;
            }

            // Doubling stops at the cap; only a hint larger than what is left goes past it, because a hint is a
            // worst case and the bytes actually committed are checked in Advance.
            long grown = Math.Min(Math.Max((long)written + Math.Max(wanted, MinimumGrowth), buffer.Length * 2L), capBytes);
            long size = Math.Max(grown, (long)written + wanted);
            if (size > Array.MaxLength)
            {
                throw new InvalidDataException(
                    $"{SqliteSourceOpenPolicy.ExportCapExceededCode}: export is larger than {capBytes} bytes.");
            }

            Array.Resize(ref buffer, (int)size);
        }
    }
}
