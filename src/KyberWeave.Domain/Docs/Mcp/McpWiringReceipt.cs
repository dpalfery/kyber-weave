using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>How the repair wrote a file, which is what decides how removal undoes it.</summary>
public enum McpWiringWriteMode
{
    /// <summary>The file did not exist, so removal deletes it.</summary>
    Created,

    /// <summary>Entries were inserted into an existing file, so removal restores its bytes.</summary>
    Inserted
}

/// <summary>One file the repair wrote, and enough of its state to undo that write.</summary>
/// <param name="Path">The repository-relative file, with <c>/</c> separators.</param>
/// <param name="Mode">Whether the repair created the file or inserted into it.</param>
/// <param name="Servers">The server names written, in the order they were inserted.</param>
/// <param name="PreSha256">The digest of the file before the write, or null for a created file.</param>
/// <param name="PostSha256">The digest of the file as the repair left it.</param>
/// <param name="WrittenAtUtc">When the write happened, in UTC.</param>
/// <param name="PreContent">
/// The file's bytes before an insertion, base64-encoded. A digest proves a restore is safe but
/// cannot produce the bytes to restore, so the pre-write content travels with the record; without
/// it "restores the pre-write state" (5.8) would be a promise the removal path could not keep.
/// </param>
public sealed record McpWiringReceiptRecord(
    string Path,
    McpWiringWriteMode Mode,
    IReadOnlyList<string> Servers,
    string? PreSha256,
    string PostSha256,
    DateTimeOffset WrittenAtUtc,
    string? PreContent = null);

/// <summary>
/// The writer's local undo record: which files the repair wrote, and what they looked like before.
/// </summary>
/// <remarks>
/// <para>
/// The receipt lives at a fixed repository-relative path, gitignored, because it is the writer's
/// own undo record and nobody else needs it: teammates receive the committed configuration file and
/// their removal path is <c>git revert</c> (DD13). One record per file, latest write wins — a second
/// record for the same path would leave the removal path guessing which write it is undoing.
/// </para>
/// <para>
/// Both digests are recorded because they answer different questions. The post-write digest is what
/// lets removal tell its own write from a foreign edit, so a file someone has since changed is
/// refused rather than restored over (5.9); the pre-write digest is what lets removal prove the
/// bytes it is about to restore are the bytes it captured.
/// </para>
/// <para>
/// A receipt that cannot be read — or that does not match the schema this code writes, down to a null
/// file list or a record with no path — is treated as empty rather than as an error. Nothing can be
/// proven from it, and an empty receipt refuses every removal — which is the safe direction:
/// Kyber-Weave only removes what it can show it wrote.
/// </para>
/// </remarks>
public sealed class McpWiringReceipt
{
    /// <summary>Where the receipt lives, relative to the repository root.</summary>
    public const string RelativePath = ".kyber-weave/receipts/mcp-wiring.json";

    /// <summary>The schema this code writes and understands.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Removal was refused: there is no record, or the file changed since the write.</summary>
    public const string DriftCode = "KW-MCP-WIRE-006";

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>One record per file the repair wrote, in the order the writes happened.</summary>
    public IReadOnlyList<McpWiringReceiptRecord> Files { get; init; } = [];

    /// <summary>The absolute path of the receipt for a repository.</summary>
    /// <param name="repositoryRoot">The repository root, as given by the caller.</param>
    public static string PathFor(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        return Path.Combine(
            repositoryRoot,
            ".kyber-weave",
            "receipts",
            "mcp-wiring.json");
    }

    /// <summary>Reads the receipt, or answers with an empty one when none can be read.</summary>
    /// <param name="repositoryRoot">The repository whose receipt is read.</param>
    public static McpWiringReceipt Load(string repositoryRoot)
    {
        string path = PathFor(repositoryRoot);

        if (!File.Exists(path))
            return new McpWiringReceipt();

        try
        {
            McpWiringReceiptDocument? document = JsonSerializer.Deserialize<McpWiringReceiptDocument>(
                File.ReadAllText(path),
                SerializerOptions);

            // A receipt written by another schema is not this code's to interpret: reading it as
            // if it were would put a restore behind a record this code cannot check.
            if (document is null || document.Version != SchemaVersion)
                return new McpWiringReceipt();

            return WellFormed(document.Files) is { } records
                ? new McpWiringReceipt { Files = records }
                : new McpWiringReceipt();
        }
        catch (IOException)
        {
            return new McpWiringReceipt();
        }
        catch (UnauthorizedAccessException)
        {
            return new McpWiringReceipt();
        }
        catch (JsonException)
        {
            return new McpWiringReceipt();
        }
    }

    /// <summary>
    /// Whether a path is a repository-relative name this code may combine with a root: one that has no
    /// root of its own, and no segment that climbs out of the tree.
    /// </summary>
    /// <param name="relativePath">The path as a record, or a catalog row, spells it.</param>
    /// <remarks>
    /// The receipt is local state on disk, so its records are worth exactly what that file is worth —
    /// which is nothing: a hand-edited or corrupted receipt can name <c>../../somewhere/else</c> or an
    /// absolute path, and a removal that combined it with the root would restore or delete bytes
    /// outside the repository it was asked to repair (2.6, 5.2).
    /// </remarks>
    internal static bool RepositoryRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return false;

        foreach (string segment in relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The records of a document whose shape matches the schema, or null when it does not.
    /// </summary>
    /// <param name="files">The document's file list, exactly as it was deserialized.</param>
    /// <remarks>
    /// <para>
    /// Nothing downstream re-checks what it reads: the removal path combines a record's path into a
    /// filesystem path, enumerates its server list into the report, and compares its digest. A
    /// document holding <c>"files": null</c>, or a record whose path or server list is null, would
    /// therefore throw somewhere after this code said it had read a receipt — so the shape is
    /// checked once, here, where refusing still means nothing was touched. The whole document is
    /// rejected rather than the offending record dropped: a receipt whose shape this code cannot
    /// trust is not a receipt it can prove anything from, and an empty receipt refuses every
    /// removal, which is the safe direction.
    /// </para>
    /// <para>
    /// Every value is held to the rule that produced it, which is what makes the check a schema
    /// rather than a null guard. The loader refuses a declared server whose name is blank, and a
    /// record whose server list is empty cannot describe a write this code performs, because it
    /// records a file only after at least one server was written to it. A digest and a payload are
    /// compared by value, so one that says nothing matches nothing and would surface later as a drift
    /// refusal about a file nobody edited. A timestamp is present on every record this code writes,
    /// and a document without one did not come from a write it can describe. <c>IsNullOrEmpty</c>
    /// answered only the null half of each: a whitespace-only name or digest loaded as proven and was
    /// then enumerated into the removal report and the restore.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<McpWiringReceiptRecord>? WellFormed(
        IReadOnlyList<McpWiringReceiptRecord>? files)
    {
        if (files is null)
            return null;

        // A null element of the array is a shape the schema does not admit either, and it is the one
        // the nullable annotation on the list cannot exclude.
        foreach (McpWiringReceiptRecord? record in files)
        {
            if (record is null
                || !RepositoryRelative(record.Path)
                || record.Servers is null
                || record.Servers.Count == 0
                || record.Servers.Any(string.IsNullOrWhiteSpace)
                || string.IsNullOrWhiteSpace(record.PostSha256)
                || record.PreSha256 is not null && string.IsNullOrWhiteSpace(record.PreSha256)
                || record.PreContent is not null && string.IsNullOrWhiteSpace(record.PreContent)
                || record.WrittenAtUtc == default)
            {
                return null;
            }
        }

        return files;
    }

    /// <summary>The record for a file, or null when the repair never wrote it.</summary>
    /// <param name="relativePath">The repository-relative path, as the receipt names it.</param>
    public McpWiringReceiptRecord? Find(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        foreach (McpWiringReceiptRecord record in Files)
        {
            if (SamePath(record.Path, relativePath))
                return record;
        }

        return null;
    }

    /// <summary>
    /// Whether two repository-relative paths name the same file. Separators differ between the
    /// platform that wrote a receipt and the one reading it, and the path's value is the file.
    /// </summary>
    /// <remarks>
    /// Case is part of that value: on a case-sensitive filesystem <c>.mcp.json</c> and
    /// <c>.MCP.json</c> are two files, so an ignore-case match would let the drift check and the
    /// removal act on a file the record does not describe.
    /// </remarks>
    internal static bool SamePath(string left, string right) =>
        string.Equals(
            left.Replace('\\', '/'),
            right.Replace('\\', '/'),
            StringComparison.Ordinal);

    /// <summary>
    /// Writes the receipt, and reports why it could not be written instead of failing the run: a
    /// caller may already have restored files by the time it rewrites the record of them, and one that
    /// threw here would lose the report of the work it did.
    /// </summary>
    /// <param name="repositoryRoot">The repository the receipt belongs to.</param>
    /// <param name="records">Every record the receipt should hold.</param>
    /// <returns>Null when written, or the reason it was not.</returns>
    internal static string? Save(string repositoryRoot, IReadOnlyList<McpWiringReceiptRecord> records)
    {
        string path = PathFor(repositoryRoot);

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new McpWiringReceiptDocument(SchemaVersion, records),
                    SerializerOptions));

            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>The receipt's document, so the schema's own shape is what gets written.</summary>
    /// <param name="Version">The schema version.</param>
    /// <param name="Files">One record per file.</param>
    internal sealed record McpWiringReceiptDocument(int Version, IReadOnlyList<McpWiringReceiptRecord> Files);
}
