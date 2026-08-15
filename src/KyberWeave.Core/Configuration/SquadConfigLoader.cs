using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using YamlDotNet.Core;

namespace KyberWeave.Core.Configuration;

/// <summary>Loads, merges, and validates the <c>squad:</c> host configuration section.</summary>
public static partial class SquadConfigLoader
{
    /// <summary>Loads Squad configuration from a combined Kyber-Weave YAML file.</summary>
    public static SquadConfig LoadMerged(SquadConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);

        KyberWeaveYamlDocument document = KyberWeaveYamlParser.ParseFile(yamlPath);
        return Merge(defaults, document.Squad);
    }

    /// <summary>
    /// Resolves the CLI's normalized <c>X.Y.Z</c> version and rejects a different host pin.
    /// </summary>
    public static string ResolveVersion(SquadConfig config, string cliVersion)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(cliVersion);

        Match match = CliVersionPattern().Match(cliVersion);
        if (!match.Success)
        {
            throw new YamlException(
                $"The Kyber-Weave CLI version '{cliVersion}' is not a stable X.Y.Z version.");
        }

        string normalizedCliVersion = match.Groups["version"].Value;
        if (config.Version is not null &&
            !string.Equals(config.Version, normalizedCliVersion, StringComparison.Ordinal))
        {
            throw new YamlException(
                $"squad.version '{config.Version}' must exactly match the Kyber-Weave CLI " +
                $"version '{normalizedCliVersion}'.");
        }

        return normalizedCliVersion;
    }

    internal static SquadConfig Merge(SquadConfig defaults, SquadYamlSection? section)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (section is null)
            return defaults;

        string bundle = section.Bundle ?? defaults.Bundle;
        if (!string.Equals(bundle, "full", StringComparison.Ordinal))
            throw new YamlException($"squad.bundle '{bundle}' is invalid. Known bundles: full.");

        string? version = section.Version ?? defaults.Version;
        if (version is not null && !PinnedVersionPattern().IsMatch(version))
            throw new YamlException("squad.version must be an exact stable X.Y.Z version.");

        SquadTranslationMode translation = ParseTranslation(section.Translation) ?? defaults.Translation;
        IReadOnlyList<SquadTarget> targets = section.Targets is null
            ? defaults.Targets
            : ParseTargets(section.Targets, "squad.targets");
        IReadOnlyList<SquadTarget> exclusions = section.Exclusions is null
            ? defaults.Exclusions
            : ParseTargets(section.Exclusions, "squad.exclusions");

        return new SquadConfig
        {
            Bundle = bundle,
            Version = version,
            Targets = targets,
            Exclusions = exclusions,
            Translation = translation
        };
    }

    private static SquadTranslationMode? ParseTranslation(string? value)
    {
        if (value is null)
            return null;

        return value switch
        {
            "best-effort" => SquadTranslationMode.BestEffort,
            _ => throw new YamlException(
                $"squad.translation '{value}' is invalid. Known modes: best-effort.")
        };
    }

    private static IReadOnlyList<SquadTarget> ParseTargets(
        IEnumerable<string> values,
        string key)
    {
        try
        {
            return SquadTargetCatalog.Parse(values);
        }
        catch (ArgumentException ex)
        {
            throw new YamlException($"{key}: {ex.Message}");
        }
    }

    [GeneratedRegex(
        @"^(?<version>(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CliVersionPattern();

    [GeneratedRegex(
        @"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PinnedVersionPattern();
}
