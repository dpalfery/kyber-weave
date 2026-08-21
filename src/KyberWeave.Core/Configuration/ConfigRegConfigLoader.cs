using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads the <c>config-reg:</c> section of <c>kyber-weave.yml</c>.</summary>
public static class ConfigRegConfigLoader
{
    /// <summary>Key name as an operator wrote it, so diagnostics name the real line.</summary>
    private const string SectionKey = "config-reg";

    internal static ConfigRegConfig Merge(ConfigRegConfig defaults, Dictionary<string, string>? section)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (section is null)
            return defaults;

        List<ConfigRegEntry> additions = new List<ConfigRegEntry>(section.Count);
        foreach ((string name, string value) in section)
        {
            string property = name?.Trim() ?? string.Empty;
            if (!ConfigSlug.IsValid(property))
            {
                throw new YamlException(
                    $"{SectionKey} property '{name}' is not a lookup name. Use lowercase " +
                    "letters, digits and single hyphens — 'auth-design', not 'Auth Design'. " +
                    "Skills reference the name verbatim.");
            }

            string path;
            try
            {
                path = DocsRootPath.Normalize(value ?? string.Empty, $"{SectionKey}.{property}");
            }
            catch (ArgumentException ex)
            {
                throw new YamlException(ex.Message);
            }

            if (path.Length == 0)
            {
                throw new YamlException(
                    $"{SectionKey}.{property} names no path. Give a repository-relative path " +
                    "to the file or directory the property stands for.");
            }

            additions.Add(new ConfigRegEntry(property, path));
        }

        return new ConfigRegConfig { Additions = additions };
    }
}
