using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Hooks;
using YamlDotNet.Serialization;

namespace KyberWeave.Data.Hooks;

/// <summary>Reads <c>.kyber-weave/hooks.yml</c> and merges it over the built-in defaults.</summary>
internal sealed class YamlHooksConfigSource(IFileSystem fileSystem) : IHooksConfigSource
{
    private static readonly string[] TopKeys = ["time-budget-seconds", "stop-on-first-blocking", "max-output-lines", "max-output-bytes", "rules"];
    private static readonly string[] RuleKeys = ["id", "kind", "include", "exclude", "run", "timeout-seconds", "blocking", "enabled"];

    public HooksConfig Load(string repositoryRoot)
    {
        string path = Path.Combine(repositoryRoot, ".kyber-weave", "hooks.yml");
        if (!fileSystem.FileExists(path))
        {
            return HooksDefaults.Config;
        }

        object? document = new DeserializerBuilder().Build().Deserialize<object>(fileSystem.ReadAllText(path));
        if (document is null)
        {
            return HooksDefaults.Config;
        }

        if (document is not Dictionary<object, object> map)
        {
            throw new InvalidDataException("hooks.yml must be a mapping at the top level.");
        }

        return HooksConfigMerger.Merge(HooksDefaults.Config, ToOverlay(map));
    }

    private static HooksConfigOverlay ToOverlay(Dictionary<object, object> map)
    {
        List<string> warnings = [];
        foreach (string key in UnknownKeys(map, TopKeys))
        {
            warnings.Add($"hooks.yml: unknown key '{key}' ignored.");
        }

        List<HookRuleOverlay> rules = [];
        if (map.TryGetValue("rules", out object? rawRules))
        {
            if (rawRules is List<object> list)
            {
                foreach (object item in list)
                {
                    if (item is Dictionary<object, object> rule && ToRule(rule, warnings) is { } parsed)
                    {
                        rules.Add(parsed);
                    }
                    else if (item is not Dictionary<object, object>)
                    {
                        warnings.Add("hooks.yml: a rule that is not a mapping was ignored.");
                    }
                }
            }
            else
            {
                warnings.Add("hooks.yml: 'rules' must be a list and was ignored.");
            }
        }

        return new HooksConfigOverlay(
            Int(map, "time-budget-seconds", "hooks.yml", warnings),
            Bool(map, "stop-on-first-blocking", "hooks.yml", warnings),
            Int(map, "max-output-lines", "hooks.yml", warnings),
            Int(map, "max-output-bytes", "hooks.yml", warnings),
            rules,
            warnings);
    }

    private static HookRuleOverlay? ToRule(Dictionary<object, object> map, List<string> warnings)
    {
        string id = map.TryGetValue("id", out object? rawId) ? Convert.ToString(rawId, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            warnings.Add("hooks.yml: a rule without an id was ignored.");
            return null;
        }

        string scope = $"hooks.yml: rule '{id}'";
        foreach (string key in UnknownKeys(map, RuleKeys))
        {
            warnings.Add($"{scope}: unknown key '{key}' ignored.");
        }

        HookRuleKind? kind = null;
        if (map.TryGetValue("kind", out object? rawKind))
        {
            string? text = Convert.ToString(rawKind, CultureInfo.InvariantCulture);
            if (string.Equals(text, "lint", StringComparison.OrdinalIgnoreCase))
            {
                kind = HookRuleKind.Lint;
            }
            else if (string.Equals(text, "test", StringComparison.OrdinalIgnoreCase))
            {
                kind = HookRuleKind.Test;
            }
            else
            {
                warnings.Add($"{scope}: kind must be 'lint' or 'test'.");
            }
        }

        return new HookRuleOverlay(
            id,
            kind,
            Strings(map, "include", scope, warnings),
            Strings(map, "exclude", scope, warnings),
            Strings(map, "run", scope, warnings),
            Int(map, "timeout-seconds", scope, warnings),
            Bool(map, "blocking", scope, warnings),
            Bool(map, "enabled", scope, warnings));
    }

    private static IEnumerable<string> UnknownKeys(Dictionary<object, object> map, string[] known) =>
        map.Keys.Select(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? string.Empty)
            .Where(k => !known.Contains(k, StringComparer.Ordinal));

    private static int? Int(Dictionary<object, object> map, string key, string scope, List<string> warnings)
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
        {
            return parsed;
        }

        warnings.Add($"{scope}: '{key}' must be a positive whole number and was ignored.");
        return null;
    }

    private static bool? Bool(Dictionary<object, object> map, string key, string scope, List<string> warnings)
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool parsed))
        {
            return parsed;
        }

        warnings.Add($"{scope}: '{key}' must be true or false and was ignored.");
        return null;
    }

    private static List<string>? Strings(Dictionary<object, object> map, string key, string scope, List<string> warnings)
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return null;
        }

        if (value is List<object> list)
        {
            return [.. list.Select(item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty)];
        }

        warnings.Add($"{scope}: '{key}' must be a list and was ignored.");
        return null;
    }
}
