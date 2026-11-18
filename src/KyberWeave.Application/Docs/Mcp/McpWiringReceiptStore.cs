using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>The receipt kept as a JSON file in the repository, reached through the file-system seam.</summary>
/// <param name="files">The file system the receipt is read from and written to.</param>
public sealed class McpWiringReceiptStore(IWritableFileSystem files) : IMcpWiringReceipts
{
    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly IWritableFileSystem _files = files ?? throw new ArgumentNullException(nameof(files));

    /// <inheritdoc />
    public McpWiringReceipt Load(string repositoryRoot)
    {
        string path = McpWiringReceipt.PathFor(repositoryRoot);

        if (!_files.FileExists(path))
            return new McpWiringReceipt();

        try
        {
            McpWiringReceipt.McpWiringReceiptDocument? document =
                JsonSerializer.Deserialize<McpWiringReceipt.McpWiringReceiptDocument>(
                    _files.ReadAllText(path),
                    SerializerOptions);

            // A receipt written by another schema is not this code's to interpret: reading it as
            // if it were would put a restore behind a record this code cannot check.
            if (document is null || document.Version != McpWiringReceipt.SchemaVersion)
                return new McpWiringReceipt();

            return McpWiringReceipt.WellFormed(document.Files) is { } records
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

    /// <inheritdoc />
    public string? Save(string repositoryRoot, IReadOnlyList<McpWiringReceiptRecord> records)
    {
        string path = McpWiringReceipt.PathFor(repositoryRoot);

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                _files.CreateDirectory(directory);

            _files.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    new McpWiringReceipt.McpWiringReceiptDocument(McpWiringReceipt.SchemaVersion, records),
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
}
