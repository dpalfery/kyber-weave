using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Loads and parses the brief map.</summary>
public static class BriefMapLoader
{
    private const string ResourceName = "Squad.briefs.yml";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>The map shipped inside the Domain assembly.</summary>
    public static BriefMap Load()
    {
        Assembly assembly = typeof(BriefMap).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new BriefMapException($"The shipped brief map '{ResourceName}' is missing from {assembly.GetName().Name}.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses a map from its YAML text.</summary>
    /// <exception cref="BriefMapException">The text is not a valid map.</exception>
    public static BriefMap Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        MapDocument document;
        try
        {
            document = Yaml.Deserialize<MapDocument>(yaml) ?? new MapDocument();
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new BriefMapException($"The brief map is not valid YAML: {exception.Message}");
        }

        if (!string.Equals(document.Schema, BriefMap.Schema, StringComparison.Ordinal))
        {
            throw new BriefMapException($"The brief map's schema is '{document.Schema}', expected '{BriefMap.Schema}'.");
        }

        Dictionary<string, IReadOnlyList<BriefFileRule>> agents = new(StringComparer.Ordinal);
        foreach ((string agent, List<FileDocument> files) in document.Agents)
        {
            List<BriefFileRule> rules = [];
            foreach (FileDocument file in files)
            {
                if (string.IsNullOrWhiteSpace(file.File))
                {
                    throw new BriefMapException($"Agent '{agent}' has a file entry with no 'file'.");
                }

                string where = $"{agent}/{file.File}";
                Dictionary<string, BriefCondition> sections = new(StringComparer.Ordinal);
                foreach ((string heading, string when) in file.Sections)
                {
                    sections[heading] = BriefCondition.Parse(when, $"{where} § {heading}");
                }

                rules.Add(new BriefFileRule(file.File, BriefCondition.Parse(file.When ?? "always", where), sections));
            }

            agents[agent] = rules;
        }

        return new BriefMap(agents);
    }

    private sealed class MapDocument
    {
        public string? Schema { get; set; }

        public Dictionary<string, List<FileDocument>> Agents { get; set; } = [];
    }

    private sealed class FileDocument
    {
        public string? File { get; set; }

        public string? When { get; set; }

        public Dictionary<string, string> Sections { get; set; } = [];
    }
}
