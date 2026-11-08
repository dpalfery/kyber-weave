using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// Loads the <c>config-reg:</c> section of <c>kyber-weave.yml</c>, and the run settings the
/// registry publishes as mode properties beside it.
/// </summary>
public static class ConfigRegConfigLoader
{
    /// <summary>Key name as an operator wrote it, so diagnostics name the real line.</summary>
    private const string SectionKey = "config-reg";

    /// <summary>Key path of the nested setting, spelled as the operator writes it.</summary>
    private const string RunHarnessNamesKey = "run.harness-names";

    internal static ConfigRegConfig Merge(
        ConfigRegConfig defaults,
        Dictionary<string, string>? section,
        string? autonomy = null,
        RunYamlSection? run = null,
        string? stopOnToolFailure = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        string? setting = ParseAutonomy(autonomy) ?? defaults.Autonomy;

        // Absence stays null rather than becoming the default here: the resolved config is
        // what publishes a mode property, and a default nobody wrote must not be published as
        // if the operator had chosen it.
        bool? toolFailure =
            ParseBoolean(
                stopOnToolFailure,
                ConfigRegConfig.StopOnToolFailureProperty,
                "a run stops when a tool fails unless the setting says otherwise.") ??
            defaults.StopOnToolFailure;
        bool? harnessNames =
            ParseBoolean(
                run?.HarnessNames,
                RunHarnessNamesKey,
                "harness names are published unless the setting says otherwise.") ??
            defaults.RunHarnessNames;
        string? baseBranch = ParseBaseBranch(run?.BaseBranch) ?? defaults.RunBaseBranch;

        bool unchanged = setting == defaults.Autonomy
                         && toolFailure == defaults.StopOnToolFailure
                         && baseBranch == defaults.RunBaseBranch
                         && harnessNames == defaults.RunHarnessNames;
        if (section is null && unchanged)
            return defaults;

        return new ConfigRegConfig
        {
            Additions = section is null ? defaults.Additions : ReadAdditions(section),
            Autonomy = setting,
            StopOnToolFailure = toolFailure,
            RunBaseBranch = baseBranch,
            RunHarnessNames = harnessNames
        };
    }

    /// <summary>
    /// Reads the host's <c>config-reg:</c> additions, refusing a name it could not honour or a
    /// path that leaves the repository.
    /// </summary>
    private static List<ConfigRegEntry> ReadAdditions(Dictionary<string, string> section)
    {
        List<ConfigRegEntry> additions = new(section.Count);
        foreach ((string name, string value) in section)
        {
            string property = name?.Trim() ?? string.Empty;
            string? owningSetting = property switch
            {
                ConfigRegConfig.AutonomyProperty => "the top-level 'autonomy:' setting",
                ConfigRegConfig.IntakeAssessmentProperty => "the 'ontology.intake:' setting",
                ConfigRegConfig.StopOnToolFailureProperty => "the top-level 'stop-on-tool-failure:' setting",
                ConfigRegConfig.RunBaseBranchProperty => "the 'run.base-branch:' setting",
                ConfigRegConfig.RunHarnessNamesProperty => "the 'run.harness-names:' setting",
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

        return additions;
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

    /// <summary>
    /// Reads a boolean setting, refusing anything <see cref="bool.TryParse(string?, out bool)"/>
    /// does not accept. A free-form read would let <c>sometimes</c> arrive as a later
    /// <see langword="false"/>, which is the opposite of what the operator meant.
    /// </summary>
    /// <param name="value">The raw value as the operator wrote it, or null when absent.</param>
    /// <param name="setting">Key path quoted in the refusal.</param>
    /// <param name="absentMeaning">What an absent setting means, for the refusal's hint.</param>
    private static bool? ParseBoolean(string? value, string setting, string absentMeaning)
    {
        if (value is null)
            return null;

        if (bool.TryParse(value.Trim(), out bool parsed))
            return parsed;

        throw new YamlException(
            $"{setting} '{value}' is not a known setting. Use 'true' or 'false', or remove " +
            $"the key; {absentMeaning}");
    }

    /// <summary>
    /// Reads <c>run.base-branch</c>, treating a blank value as absent. A property publishing an
    /// empty branch would look configured while resolving to nothing a run can compare against.
    /// </summary>
    private static string? ParseBaseBranch(string? value)
    {
        string? branch = value?.Trim();
        return string.IsNullOrEmpty(branch) ? null : branch;
    }
}
