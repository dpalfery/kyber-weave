using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// How a <see cref="PlanTaskFile"/> is read and written, in one place so the validator that reads it and
/// the generator that writes it cannot disagree about the shape.
/// </summary>
/// <remarks>
/// <para>
/// The stored form is YAML. The model's JSON property names are the field names both forms use, so
/// the YAML is produced by converting the model's JSON graph: one list of names, whether a file is
/// written or read. The JSON form an older release wrote is still read, with a warning that names
/// the convert command, for two minor releases.
/// </para>
/// <para>
/// A YAML document is read through the same graph: YAML is a superset of JSON, so the parsed graph
/// is serialised back to JSON and the model's strict reader takes it. That keeps one strict field
/// set for both forms instead of a second, laxer YAML binding.
/// </para>
/// </remarks>
internal static partial class PlanTaskFileFormat
{
    /// <summary>True when the path names the JSON form an older release wrote.</summary>
    public static bool IsLegacyJson(string path) =>
        path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <summary>The canonical YAML text of a task file: LF line endings, one final newline.</summary>
    /// <param name="file">The file to write.</param>
    public static string Serialize(PlanTaskFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string json = JsonSerializer.Serialize(file, JsonWriteOptions);
        object? graph = Deserializer.Deserialize<object>(json);
        string yaml = Serializer.Serialize(graph);
        return yaml.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Reads a task file in either form. A malformed document throws the format's own exception,
    /// which the caller reports.
    /// </summary>
    public static PlanTaskFile? Deserialize(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        string json = IsLegacyJson(path)
            ? text
            : JsonSerializer.Serialize(NormalizeNumbers(Deserializer.Deserialize<object>(text)));
        return JsonSerializer.Deserialize<PlanTaskFile>(json, JsonReadOptions);
    }

    /// <summary>
    /// The YAML reader returns every scalar as text. The model's numeric fields are converted here,
    /// so a YAML file with the same field names as the JSON form reads through the same strict
    /// reader.
    /// </summary>
    /// <remarks>
    /// An explicit tag such as <c>!!float</c> makes the reader hand back a double instead of text, so it
    /// skips the checks on the text form below and reaches the JSON writer, which refuses a value with no
    /// JSON literal. The format's own exception is thrown here instead, so the file is reported as
    /// malformed rather than an <see cref="ArgumentException"/> escaping the reader.
    /// </remarks>
    private static object? NormalizeNumbers(object? graph)
    {
        if (graph is List<object> list)
        {
            return list.Select(NormalizeNumbers).ToList();
        }

        RefuseNonFinite(graph, null);

        if (graph is not Dictionary<object, object> map)
        {
            return graph;
        }

        Dictionary<string, object?> normalized = new(StringComparer.Ordinal);
        foreach ((object key, object value) in map)
        {
            RefuseNonFinite(key, null);
            string name = key.ToString() ?? string.Empty;
            RefuseNonFinite(value, name);
            normalized[name] = name switch
            {
                "schema-version" when value is string version
                    && int.TryParse(version, System.Globalization.CultureInfo.InvariantCulture, out int number) => number,
                "agent-confidence" when value is string confidence
                    && double.TryParse(confidence, System.Globalization.CultureInfo.InvariantCulture, out double score)
                    && double.IsFinite(score) => score,
                _ => value is Dictionary<object, object> or List<object> ? NormalizeNumbers(value) : value
            };
        }

        return normalized;
    }

    /// <summary>
    /// Refuses a number with no JSON literal, whether it is a value or a map key. A tag such as
    /// <c>!!float</c> makes the reader hand back a number instead of text, so the checks on the text form
    /// never run; a key that reached the writer as a string would become an unknown property the model
    /// ignores, and the file would validate.
    /// </summary>
    private static void RefuseNonFinite(object? value, string? name)
    {
        if (value is double number && !double.IsFinite(number))
        {
            throw NonFinite(name, number);
        }

        if (value is float single && !float.IsFinite(single))
        {
            throw NonFinite(name, single);
        }
    }

    private static JsonException NonFinite(string? name, object number) =>
        new(name is null
            ? $"the document holds '{number}', which is not a finite number."
            : $"'{name}' is '{number}', which is not a finite number.");

    /// <summary>Reads the JSON form directly, for a caller that holds text with no path.</summary>
    public static PlanTaskFile? DeserializeJson(string text) =>
        JsonSerializer.Deserialize<PlanTaskFile>(text, JsonReadOptions);

    /// <summary>True when a block of text looks like the JSON form, used by the sidecar sniff.</summary>
    public static bool LooksLikeJson(string text) => JsonStart().IsMatch(text);

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = false,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .WithIndentedSequences()
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    [GeneratedRegex(@"^\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex JsonStart();
}
