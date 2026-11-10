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
    /// <summary>
    /// Reads one VS Code settings file, telling a missing file apart from one that is present but unusable.
    /// </summary>
    /// <param name="settingsFile">The settings file path.</param>
    /// <returns>The bytes; or neither bytes nor error when the file is missing; or the reason it cannot be read.</returns>
    /// <remarks>
    /// A symbolic link whose final target does not exist is missing: <see cref="File.Exists(string)"/> is true for it,
    /// but there is nothing to read, and a write goes through it. A directory at the path is present but unusable.
    /// Every probe and the read itself are guarded, so an IO or permission failure comes back as
    /// <see cref="SettingsFileRead.Error"/> for the caller to report, never as a crash. The install commands and the
    /// doctor probe both read through here, so they agree on what they see.
    /// </remarks>
    public static SettingsFileRead ReadFile(string settingsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFile);
        try
        {
            if (Directory.Exists(settingsFile))
                return new SettingsFileRead(null, "the path is a directory, not a file");

            FileInfo info = new(settingsFile);
            bool present = info.LinkTarget is null
                ? info.Exists
                : info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true };
            return present ? new SettingsFileRead(File.ReadAllBytes(settingsFile), null) : new SettingsFileRead(null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SettingsFileRead(null, exception.Message);
        }
    }

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
    /// <returns>The state, or null when the bytes are not a JSON object even leniently.</returns>
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

    private static CopilotOverrideState? FromRoot(JsonElement root)
    {
        // VS Code loads only an object as settings; any other JSON is as unusable as text that does not parse.
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty(SettingId, out JsonElement overrides)
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

/// <summary>One settings file as read: its bytes, null when it is missing, or the error that made it unusable.</summary>
/// <param name="Document">The file's bytes; null when the file is missing or unusable.</param>
/// <param name="Error">Why a present file could not be read; null when it was read or is missing.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Performance", "CA1819:Properties should not return arrays",
    Justification = "The bytes are handed once to the JSON splice and reader, which take byte[]; no caller mutates them.")]
public sealed record SettingsFileRead(byte[]? Document, string? Error);
