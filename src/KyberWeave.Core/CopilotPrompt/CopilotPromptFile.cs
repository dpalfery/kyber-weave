using System.Text;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Core;

namespace KyberWeave.Core.CopilotPrompt;

/// <summary>
/// The Copilot system-prompt override file: the text Kyber-Weave ships, where it is installed,
/// and the check that a file on disk is usable.
/// </summary>
public static class CopilotPromptFile
{
    /// <summary>The manifest name the prompt is embedded under; see the Core project file.</summary>
    private const string ResourceName = "CopilotPrompt.system-prompt.yaml";

    /// <summary>The only top-level keys VS Code reads from a prompt file.</summary>
    private const string SystemPromptKey = "systemPrompt";
    private const string ToolDescriptionsKey = "toolDescriptions";

    /// <summary>
    /// Returns the system-prompt YAML text that ships inside the Kyber-Weave binary.
    /// </summary>
    /// <returns>The embedded YAML text with a top-level <c>systemPrompt</c> key.</returns>
    public static string EmbeddedText()
    {
        using Stream stream = typeof(CopilotPromptFile).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ResourceName}' is missing from this binary.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Returns the path where the system-prompt YAML file is installed for one user.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The absolute path <c>&lt;home&gt;/.kyber-weave/copilot/system-prompt.yaml</c>.</returns>
    public static string InstalledPath(string homeDirectory) =>
        Path.Combine(homeDirectory, ".kyber-weave", "copilot", "system-prompt.yaml");

    /// <summary>
    /// Checks that YAML text is a usable system-prompt file.
    /// </summary>
    /// <param name="yamlText">The YAML text to check.</param>
    /// <returns>Null when the text is valid; otherwise the reason it is not.</returns>
    /// <remarks>
    /// VS Code treats a bad file as no file at all and silently falls back to Copilot's default prompt, so
    /// this check is the only place a broken prompt is named. Only the two keys VS Code reads are allowed:
    /// an unknown key is refused rather than ignored, because a typo there would silently do nothing.
    /// </remarks>
    public static string? Validate(string yamlText)
    {
        ArgumentNullException.ThrowIfNull(yamlText);

        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yamlText));
        }
        catch (YamlException ex)
        {
            return $"it is not valid YAML: {ex.Message}";
        }

        if (stream.Documents.Count != 1)
            return "it must hold exactly one YAML document";
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            return "its top level must be a mapping of keys";

        bool hasSystemPrompt = false;
        foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
        {
            string key = entry.Key is YamlScalarNode keyScalar ? keyScalar.Value ?? string.Empty : string.Empty;
            if (key == SystemPromptKey)
            {
                if (entry.Value is not YamlScalarNode prompt || string.IsNullOrWhiteSpace(prompt.Value))
                    return $"{SystemPromptKey} must be a non-empty string";
                hasSystemPrompt = true;
            }
            else if (key == ToolDescriptionsKey)
            {
                if (entry.Value is not YamlMappingNode)
                    return $"{ToolDescriptionsKey} must be a mapping of tool names to descriptions";
            }
            else
            {
                return $"'{key}' is not a key VS Code reads; only {SystemPromptKey} and {ToolDescriptionsKey} are allowed";
            }
        }

        return hasSystemPrompt ? null : $"{SystemPromptKey} is missing";
    }

    /// <summary>
    /// True when two paths name the same file: both are made absolute and normalised, trailing separators are
    /// dropped, and case is ignored only on Windows. The doctor's home check uses the same rule.
    /// </summary>
    /// <param name="left">One path.</param>
    /// <param name="right">The other path.</param>
    /// <returns>True when the normalised paths are equal; false when either cannot be normalised.</returns>
    /// <remarks>
    /// Normalising makes a settings entry written with forward slashes on Windows match. Case is not folded on macOS,
    /// because a case-sensitive APFS volume can hold two files whose names differ only in case; there a mismatch
    /// only makes install refuse, which is the safe direction.
    /// </remarks>
    public static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
