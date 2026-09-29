using System.Text;
using System.Text.Json;

namespace KyberWeave.Core.Utilities.StatusLine;

/// <summary>Reads and writes the Kyber Utilities status-line receipt for one harness.</summary>
/// <remarks>
/// The receipt is state, not a deployed artifact, so it lives in the per-user Kyber directory —
/// <c>~/.kyber-weave/utilities/statusline/&lt;target&gt;.receipt.json</c> — rather than inside the
/// staging root. Two reasons. <c>status</c> and <c>doctor</c> must still be able to report drift
/// after an operator deletes a staging root by hand, and a file that records what Kyber owns must
/// never itself look like something Kyber deployed, because <c>remove</c> acts on exactly the paths
/// the receipt names.
///
/// <para>
/// The JSON is written through <see cref="Utf8JsonWriter"/> and validated on read, following
/// <c>SquadStateStore</c>'s receipt handling, so a hand-edited, truncated, or foreign file is
/// reported with a remedy rather than silently trusted. The paths a receipt records are re-resolved
/// through <c>SquadPathPolicy.ResolveFile</c> before any plan touches them, so a tampered receipt
/// cannot make a removal reach outside the staging root.
/// </para>
/// </remarks>
internal sealed class StatusLineReceiptStore
{
    /// <summary>The one receipt schema. A file that declares anything else is refused, not guessed at.</summary>
    internal const string Schema = "kyber-utilities.statusline.receipt/v1";

    private const string StateDirectoryRelativePath = ".kyber-weave/utilities/statusline";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n"
    };

    private readonly StatusLineTargetRoots _roots;
    private readonly StatusLineTarget _target;

    internal StatusLineReceiptStore(StatusLineTargetRoots roots, StatusLineTarget target)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = roots;
        _target = target;
        ReceiptPath = Path.Combine(
            roots.HomeDirectory,
            StateDirectoryRelativePath.Replace('/', Path.DirectorySeparatorChar),
            TargetToken(target) + ".receipt.json");
    }

    /// <summary>The absolute path of this target's receipt file, whether or not it exists.</summary>
    internal string ReceiptPath { get; }

    /// <summary>Reads the receipt, or returns <see langword="null"/> when this target owns nothing.</summary>
    internal StatusLineReceipt? Read()
    {
        return File.Exists(ReceiptPath)
            ? Deserialize(File.ReadAllText(ReceiptPath, Encoding.UTF8))
            : null;
    }

    internal void Write(StatusLineReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        Directory.CreateDirectory(
            Path.GetDirectoryName(ReceiptPath) ??
            throw new InvalidOperationException($"Receipt path '{ReceiptPath}' has no parent directory."));
        File.WriteAllText(
            ReceiptPath,
            Serialize(receipt),
            new UTF8Encoding(false));
    }

    /// <summary>Removes the receipt, so a completed removal leaves no ownership record behind.</summary>
    internal void Delete()
    {
        if (File.Exists(ReceiptPath))
            File.Delete(ReceiptPath);
    }

    private string Serialize(StatusLineReceipt receipt)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteString("target", TargetToken(_target));
            writer.WriteStartArray("files");
            foreach (StatusLineOwnedFile file in receipt.Files)
            {
                ArgumentNullException.ThrowIfNull(file);
                if (file.Target != _target)
                {
                    throw new StatusLineDeploymentConflictException(
                        $"The receipt for '{TargetToken(_target)}' cannot record '{file.RelativePath}' " +
                        $"as owned by '{TargetToken(file.Target)}'. One receipt covers one harness.");
                }

                writer.WriteStartObject();
                writer.WriteString("relativePath", file.RelativePath);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private StatusLineReceipt Deserialize(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"The status-line receipt at '{ReceiptPath}' must be a JSON object. Delete it and " +
                    "deploy again to recreate it.");
            }

            string schema = RequireString(root, "schema");
            if (!string.Equals(schema, Schema, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The status-line receipt at '{ReceiptPath}' declares schema '{schema}', not " +
                    $"'{Schema}'. Delete it and deploy again to recreate it.");
            }

            string target = RequireString(root, "target");
            if (!string.Equals(target, TargetToken(_target), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The status-line receipt at '{ReceiptPath}' records target '{target}', not " +
                    $"'{TargetToken(_target)}'. Delete it and deploy again to recreate it.");
            }

            List<StatusLineOwnedFile> files = [];
            foreach (JsonElement element in RequireArray(root, "files").EnumerateArray())
            {
                string relativePath = RequireString(element, "relativePath");
                string sha256 = RequireString(element, "sha256");
                if (!IsSha256Hex(sha256))
                {
                    throw new InvalidDataException(
                        $"The status-line receipt at '{ReceiptPath}' records '{sha256}' as the digest " +
                        $"of '{relativePath}', which is not a SHA-256 hex string.");
                }

                files.Add(new StatusLineOwnedFile(relativePath, sha256, _target));
            }

            return new StatusLineReceipt(files);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            throw new InvalidDataException(
                $"The status-line receipt at '{ReceiptPath}' is not readable: {exception.Message}. " +
                "Delete it and deploy again to recreate it.",
                exception);
        }
    }

    private string RequireString(JsonElement element, string propertyName)
    {
        JsonElement property = RequireProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ??
              throw new InvalidDataException(
                  $"The status-line receipt at '{ReceiptPath}' has a null '{propertyName}'.")
            : throw new InvalidDataException(
                $"The status-line receipt at '{ReceiptPath}' needs '{propertyName}' to be a string.");
    }

    private JsonElement RequireArray(JsonElement element, string propertyName)
    {
        JsonElement property = RequireProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.Array
            ? property
            : throw new InvalidDataException(
                $"The status-line receipt at '{ReceiptPath}' needs '{propertyName}' to be an array.");
    }

    private JsonElement RequireProperty(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out JsonElement property)
            ? property
            : throw new InvalidDataException(
                $"The status-line receipt at '{ReceiptPath}' has no '{propertyName}' property.");
    }

    private static bool IsSha256Hex(string value)
    {
        if (value.Length != 64)
            return false;

        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
                return false;
        }

        return true;
    }

    private static string TargetToken(StatusLineTarget target)
    {
        return target switch
        {
            StatusLineTarget.Claude => "claude",
            StatusLineTarget.Agy => "agy",
            StatusLineTarget.Pi => "pi",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target,
                "No status-line receipt name exists for this target.")
        };
    }
}
