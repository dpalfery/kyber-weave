using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads the <c>config-reg:</c> section of <c>kyber-weave.yml</c>.</summary>
public static class ConfigRegConfigLoader
{
    /// <summary>Key name as an operator wrote it, so diagnostics name the real line.</summary>
    private const string SectionKey = "config-reg";

    internal static ConfigRegConfig Merge(
        ConfigRegConfig defaults,
        Dictionary<string, string>? section,
        string? autonomy = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        string? setting = ParseAutonomy(autonomy) ?? defaults.Autonomy;
        if (section is null)
        {
            return setting == defaults.Autonomy
                ? defaults
                : new ConfigRegConfig { Additions = defaults.Additions, Autonomy = setting };
        }

        List<ConfigRegEntry> additions = new(section.Count);
        foreach ((string name, string value) in section)
        {
            string property = name?.Trim() ?? string.Empty;
            string? owningSetting = property switch
            {
                ConfigRegConfig.AutonomyProperty => "the top-level 'autonomy:' setting",
                ConfigRegConfig.IntakeAssessmentProperty => "the 'ontology.intake:' setting",
                _ => null
            };
            if (owningSetting is not null)
            {
                throw new YamlException(
                    $"{SectionKey} property '{property}' is reserved: it is {owningSetting}, " +
                    "a mode word rather than a path. Set it there, or choose another name.");
            }

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

        return new ConfigRegConfig { Additions = additions, Autonomy = setting };
    }

    private static string? ParseAutonomy(string? value)
    {
        if (value is null)
            return null;

        string setting = value.Trim();
        if (setting is ConfigRegConfig.SupervisedAutonomy or ConfigRegConfig.AutonomousAutonomy)
            return setting;

        throw new YamlException(
            $"{ConfigRegConfig.AutonomyProperty} '{value}' is not a known setting. Use " +
            $"'{ConfigRegConfig.SupervisedAutonomy}' or '{ConfigRegConfig.AutonomousAutonomy}', " +
            "or remove the key; agents treat an absent setting as supervised.");
    }
}
