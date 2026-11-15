using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// How a <see cref="PlanTaskFile"/> is read and written, in one place so the validator that reads it and
/// the generator that writes it cannot disagree about the shape.
/// </summary>
internal static class PlanTaskFileJson
{
    /// <summary>
    /// Strict reading: the names are pinned per property, so a document that misspells one loses that field
    /// and fails to parse rather than being silently read as a different file.
    /// </summary>
    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        // Prompts carry quotes, angle brackets and backticks; escaping them would make the file unreadable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The canonical text of a task file: indented, LF line endings, one final newline.</summary>
    /// <param name="file">The file to write.</param>
    public static string Serialize(PlanTaskFile file) =>
        JsonSerializer.Serialize(file, WriteOptions) + "\n";
}
