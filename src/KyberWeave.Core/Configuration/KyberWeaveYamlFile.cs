namespace KyberWeave.Core.Configuration;

/// <summary>The file-reading half of <see cref="KyberWeaveYamlParser"/>: reads a config file and parses it.</summary>
/// <remarks>
/// The parse is pure and lives in the Domain layer; reading the file is not, so it stays here until
/// the adapter that reads host configuration moves to Data.
/// </remarks>
internal static class KyberWeaveYamlFile
{
    public static KyberWeaveYamlDocument ParseFile(string yamlPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        string yaml = File.ReadAllText(yamlPath);
        return KyberWeaveYamlParser.Parse(yaml);
    }
}
