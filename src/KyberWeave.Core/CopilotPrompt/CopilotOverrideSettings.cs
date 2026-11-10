using System.Text.Json;

namespace KyberWeave.Core.CopilotPrompt;

/// <summary>
/// What one VS Code settings file says about the Copilot prompt override.
/// </summary>
/// <param name="StarEntryPresent">Whether the file holds a <c>*</c> entry under the override setting.</param>
/// <param name="StarPromptFile">The <c>promptOverrideFile</c> of that entry; null when the entry or the field is absent.</param>
/// <param name="ModelSpecificIds">The model ids with their own entry. Each one takes precedence over <c>*</c> for that model.</param>
public sealed record CopilotOverrideState(bool StarEntryPresent, string? StarPromptFile, IReadOnlyList<string> ModelSpecificIds);

/// <summary>
/// Reads the Copilot prompt override out of a VS Code settings file without changing it.
/// </summary>
/// <remarks>
/// VS Code settings are JSON with comments and trailing commas, so the read is lenient. The read never
/// writes, and it never re-serialises: a splice decision is made on these facts, and the bytes are
/// changed only by <c>JsonServerSplice</c>.
/// </remarks>
public static class CopilotOverrideSettings
{
    /// <summary>The user setting that holds the per-model capability overrides.</summary>
    public const string SettingId = VsCodeUserSettingsPaths.SettingId;

    /// <summary>The entry that applies to every model that has no entry of its own.</summary>
    public const string StarEntry = "*";

    private const string PromptOverrideFileKey = "promptOverrideFile";

    private static readonly JsonDocumentOptions LenientOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads the override state from a settings file's bytes.</summary>
    /// <param name="document">The settings file's bytes, byte-order mark included.</param>
    /// <returns>The state, or null when the bytes are not JSON even leniently.</returns>
    public static CopilotOverrideState? Read(byte[] document)
    {
        ArgumentNullException.ThrowIfNull(document);

        bool hasBom = document.Length >= 3 && document[0] == 0xEF && document[1] == 0xBB && document[2] == 0xBF;
        ReadOnlyMemory<byte> body = hasBom ? document.AsMemory(3) : document.AsMemory();

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(body, LenientOptions);
            return FromRoot(parsed.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CopilotOverrideState FromRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(SettingId, out JsonElement overrides)
            || overrides.ValueKind != JsonValueKind.Object)
        {
            return new CopilotOverrideState(false, null, []);
        }

        bool starPresent = overrides.TryGetProperty(StarEntry, out JsonElement star);
        string? promptFile = null;
        if (starPresent
            && star.ValueKind == JsonValueKind.Object
            && star.TryGetProperty(PromptOverrideFileKey, out JsonElement file)
            && file.ValueKind == JsonValueKind.String)
        {
            promptFile = file.GetString();
        }

        List<string> modelIds = [];
        foreach (JsonProperty property in overrides.EnumerateObject())
        {
            if (property.Name != StarEntry)
                modelIds.Add(property.Name);
        }

        return new CopilotOverrideState(starPresent, promptFile, modelIds);
    }
}
