using KyberWeave.Core.Parsing;

namespace KyberWeave.Core.Configuration;

/// <summary>Shared YAML parse helpers for <c>.kyber-weave/kyber-weave.yml</c>.</summary>
/// <remarks>
/// This half is pure; <c>FileKyberWeaveConfigRepository</c> reads the file and delegates here.
/// </remarks>
internal static class KyberWeaveYamlParser
{
    public const string DefaultFileName = "kyber-weave.yml";

    /// <summary>Repo-root folder holding Kyber-Weave host configuration.</summary>
    public const string DefaultDirectoryName = ".kyber-weave";

    public static KyberWeaveYamlDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        // YamlDotNet raises YamlException (and subclasses SyntaxErrorException /
        // SemanticErrorException) for parse failures; those propagate unchanged.
        // Non-YAML exceptions are not wrapped — they surface as programming/runtime errors.
        return MarkdownFrontmatterReader.Deserializer.Deserialize<KyberWeaveYamlDocument?>(yaml)
               ?? new KyberWeaveYamlDocument();
    }
}
