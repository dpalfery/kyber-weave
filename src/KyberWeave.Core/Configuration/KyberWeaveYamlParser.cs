using KyberWeave.Core.Parsing;

namespace KyberWeave.Core.Configuration;

/// <summary>Shared YAML parse helpers for <c>kyber-weave.yml</c>.</summary>
internal static class KyberWeaveYamlParser
{
    public const string DefaultFileName = "kyber-weave.yml";

    public static KyberWeaveYamlDocument ParseFile(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        var yaml = File.ReadAllText(yamlPath);
        return Parse(yaml);
    }

    public static KyberWeaveYamlDocument Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        // YamlDotNet raises YamlException (and subclasses SyntaxErrorException /
        // SemanticErrorException) for parse failures; those propagate unchanged.
        // Non-YAML exceptions are not wrapped — they surface as programming/runtime errors.
        return MarkdownFrontmatterReader.Deserializer.Deserialize<KyberWeaveYamlDocument>(yaml)
               ?? new KyberWeaveYamlDocument();
    }
}
