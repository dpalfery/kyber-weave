using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Parsing;
using YamlDotNet.Core;

namespace KyberWeave.Core.Arbiter;

/// <summary>Reads the per-user Arbiter override from the operator's home directory.</summary>
/// <remarks>
/// The override lives at <c>~/.config/kyber-weave/arbiter.yml</c> and may hold
/// <c>provider:</c> and nothing else; its fields replace the repository's
/// <c>arbiter.provider</c> field by field. The home directory is injected rather than
/// read from the environment so hooks, servers and tests resolve the same file. A
/// missing file means no override. The key never appears here — this file carries only
/// non-secret provider fields — so diagnostics may name the file freely.
/// </remarks>
public static class ArbiterUserSettings
{
    /// <summary>Locates the override file under an injected home directory.</summary>
    public static string GetPath(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return Path.Combine(homeDirectory, ".config", "kyber-weave", "arbiter.yml");
    }

    /// <summary>
    /// Parses the override file's text into its <c>provider:</c> mapping and checks that it holds nothing else.
    /// </summary>
    /// <param name="yaml">The file's text.</param>
    /// <param name="path">The file's path, named in every failure.</param>
    /// <returns>The <c>provider:</c> mapping, or null when the file declares none.</returns>
    /// <exception cref="YamlException">The text is malformed or holds a key other than <c>provider</c>.</exception>
    internal static ArbiterProviderYaml? ParseProvider(string yaml, string path)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Dictionary<string, object?>? raw;
        ArbiterYamlSection? section;
        try
        {
            raw = MarkdownFrontmatterReader.Deserializer.Deserialize<Dictionary<string, object?>?>(yaml);
            section = MarkdownFrontmatterReader.Deserializer.Deserialize<ArbiterYamlSection?>(yaml);
        }
        catch (YamlException exception)
        {
            throw new YamlException(
                $"[{RuleValidator.MalformedSection}] The user override '{path}' is malformed. {exception.Message}",
                exception);
        }

        // The deserializer ignores unmatched properties by design, so unknown top-level
        // keys are detected against the raw mapping instead of the bound section.
        if (raw is not null)
        {
            foreach (string key in raw.Keys)
            {
                if (!string.Equals(key, "provider", StringComparison.Ordinal))
                {
                    throw new YamlException(
                        $"[{RuleValidator.UserOverrideKey}] The user override '{path}' holds '{key}'; it may hold provider and nothing else.");
                }
            }
        }

        IReadOnlyList<Diagnostic> findings = RuleValidator.ValidateUserOverride(section, path);
        if (findings.Count > 0)
        {
            throw new YamlException(
                $"[{RuleValidator.UserOverrideKey}] The user override '{path}' holds '{findings[0].Subject}'; it may hold provider and nothing else.");
        }

        return section?.Provider;
    }
}
