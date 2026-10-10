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
    /// Applies the user override to the repository provider, field by field. Returns the
    /// repository provider unchanged when the file is absent.
    /// </summary>
    public static ArbiterProviderConfig ApplyTo(ArbiterProviderConfig repository, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArbiterProviderYaml? userProvider = LoadProvider(homeDirectory);
        return ArbiterConfigLoader.ApplyUserProviderOverride(repository, userProvider);
    }

    /// <summary>
    /// Loads the override's <c>provider:</c> mapping, or null when the file is absent.
    /// </summary>
    internal static ArbiterProviderYaml? LoadProvider(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string path = GetPath(homeDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        string yaml;
        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new YamlException(
                $"[{RuleValidator.MalformedSection}] The user override '{path}' could not be read.",
                exception);
        }

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
