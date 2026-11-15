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

    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    private static readonly Reader<int?> PositiveInt = new(
        "a positive whole number",
        value => int.TryParse(Text(value), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0 ? number : null);

    private static readonly Reader<bool?> Boolean = new(
        "true or false",
        value => bool.TryParse(Text(value), out bool flag) ? flag : null);

    private static readonly Reader<List<string>?> StringList = new(
        "a list",
        value => value is List<object> list ? [.. list.Select(Text)] : null);

    public HooksConfig Load(string repositoryRoot)
    {
        string path = Path.Combine(repositoryRoot, ".kyber-weave", "hooks.yml");
        if (!fileSystem.FileExists(path))
        {
            return HooksDefaults.Config;
        }

        object? document = Deserializer.Deserialize<object>(fileSystem.ReadAllText(path));
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
            if (rawRules is not List<object> list)
            {
                warnings.Add("hooks.yml: 'rules' must be a list and was ignored.");
            }
            else
            {
                foreach (object item in list)
                {
                    if (item is not Dictionary<object, object> rule)
                    {
                        warnings.Add("hooks.yml: a rule that is not a mapping was ignored.");
                    }
                    else if (ToRule(rule, warnings) is { } parsed)
                    {
                        rules.Add(parsed);
                    }
                }
            }
        }

        return new HooksConfigOverlay(
            Read(map, "time-budget-seconds", "hooks.yml", warnings, PositiveInt),
            Read(map, "stop-on-first-blocking", "hooks.yml", warnings, Boolean),
            Read(map, "max-output-lines", "hooks.yml", warnings, PositiveInt),
            Read(map, "max-output-bytes", "hooks.yml", warnings, PositiveInt),
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
            if (Enum.TryParse(Text(rawKind), ignoreCase: true, out HookRuleKind parsedKind) && Enum.IsDefined(parsedKind))
            {
                kind = parsedKind;
            }
            else
            {
                warnings.Add($"{scope}: kind must be 'lint' or 'test'.");
            }
        }

        return new HookRuleOverlay(
            id,
            kind,
            Read(map, "include", scope, warnings, StringList),
            Read(map, "exclude", scope, warnings, StringList),
            Read(map, "run", scope, warnings, StringList),
            Read(map, "timeout-seconds", scope, warnings, PositiveInt),
            Read(map, "blocking", scope, warnings, Boolean),
            Read(map, "enabled", scope, warnings, Boolean));
    }

    private static IEnumerable<string> UnknownKeys(Dictionary<object, object> map, string[] known) =>
        map.Keys.Select(k => Convert.ToString(k, CultureInfo.InvariantCulture) ?? string.Empty)
            .Where(k => !known.Contains(k, StringComparer.Ordinal));

    private static T Read<T>(Dictionary<object, object> map, string key, string scope, List<string> warnings, Reader<T> reader)
    {
        if (!map.TryGetValue(key, out object? value))
        {
            return default!;
        }

        T parsed = reader.Parse(value);
        if (parsed is null)
        {
            warnings.Add($"{scope}: '{key}' must be {reader.Expected} and was ignored.");
        }

        return parsed;
    }

    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private sealed record Reader<T>(string Expected, Func<object, T> Parse);
}
