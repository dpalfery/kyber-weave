using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Hooks;
using YamlDotNet.Serialization;

namespace KyberWeave.Data.Hooks;

/// <summary>
/// Reads the edit hook's two configuration files and merges them over the built-in defaults: the repository's
/// <c>.kyber-weave/hooks.yml</c> first, which may only tune the built-in rules, then the user's
/// <c>~/.kyber-weave/hooks.yml</c>, which may also add rules whose commands the operator wrote.
/// </summary>
internal sealed class YamlHooksConfigSource(IFileSystem fileSystem, IProcessEnvironment environment) : IHooksConfigSource
{
    private static readonly string[] TopKeys = ["time-budget-seconds", "stop-on-first-blocking", "max-output-lines", "max-output-bytes", "rules"];
    private static readonly string[] RuleKeys = ["id", "kind", "include", "exclude", "run", "timeout-seconds", "blocking", "enabled"];

    private static readonly string[] ListKeys = ["include", "exclude", "run"];

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
        // The repository file is read first and may only tune what ships; the user file is read second and may add
        // a rule, because a command is the operator's decision and not something a cloned repository can set.
        HooksConfig config = LoadFile(HooksConfigPath.Repository(repositoryRoot), "hooks.yml", HookConfigSource.Repository, HooksDefaults.Config);
        return LoadFile(HooksConfigPath.User(environment.HomeDirectory), "user hooks.yml", HookConfigSource.User, config);
    }

    private HooksConfig LoadFile(string path, string displayName, HookConfigSource source, HooksConfig current)
    {
        if (!fileSystem.FileExists(path))
        {
            return current;
        }

        object? document = Deserializer.Deserialize<object>(fileSystem.ReadAllText(path));
        if (document is null)
        {
            return current;
        }

        if (document is not Dictionary<object, object> map)
        {
            throw new InvalidDataException($"{displayName} must be a mapping at the top level.");
        }

        return HooksConfigMerger.Merge(current, ToOverlay(map, displayName, source), source);
    }

    private static HooksConfigOverlay ToOverlay(Dictionary<object, object> map, string displayName, HookConfigSource source)
    {
        List<string> warnings = [];
        foreach (string key in UnknownKeys(map, TopKeys))
        {
            warnings.Add($"{displayName}: unknown key '{key}' ignored.");
        }

        List<HookRuleOverlay> rules = [];
        if (map.TryGetValue("rules", out object? rawRules))
        {
            if (rawRules is not List<object> list)
            {
                warnings.Add($"{displayName}: 'rules' must be a list and was ignored.");
            }
            else
            {
                foreach (object item in list)
                {
                    if (item is not Dictionary<object, object> rule)
                    {
                        warnings.Add($"{displayName}: a rule that is not a mapping was ignored.");
                    }
                    else if (ToRule(rule, displayName, source, warnings) is { } parsed)
                    {
                        rules.Add(parsed);
                    }
                }
            }
        }

        return new HooksConfigOverlay(
            Read(map, "time-budget-seconds", displayName, warnings, PositiveInt),
            Read(map, "stop-on-first-blocking", displayName, warnings, Boolean),
            Read(map, "max-output-lines", displayName, warnings, PositiveInt),
            Read(map, "max-output-bytes", displayName, warnings, PositiveInt),
            rules,
            warnings);
    }

    private static HookRuleOverlay? ToRule(
        Dictionary<object, object> map,
        string displayName,
        HookConfigSource source,
        List<string> warnings)
    {
        string id = map.TryGetValue("id", out object? rawId) ? Convert.ToString(rawId, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            warnings.Add($"{displayName}: a rule without an id was ignored.");
            return null;
        }

        string scope = $"{displayName}: rule '{id}'";

        // The trust rule is stated where the operator reads it rather than only in the merge, so a repository file
        // that names a command says why it was refused at the point it did so.
        if (source != HookConfigSource.User && map.TryGetValue("run", out _))
        {
            warnings.Add(
                $"{scope}: a repository file may tune the built-in rules but cannot name a command; " +
                "the command was ignored. Put a new rule in ~/.kyber-weave/hooks.yml.");
        }
        foreach (string key in UnknownKeys(map, RuleKeys))
        {
            warnings.Add($"{scope}: unknown key '{key}' ignored.");
        }

        foreach (string key in ListKeys)
        {
            if (map.TryGetValue(key, out object? value) && value is List<object> items && items.Any(item => item is null or Dictionary<object, object> or List<object>))
            {
                warnings.Add($"{scope}: '{key}' must hold only plain values (no mappings, lists or empty items), so the rule was ignored.");
                return null;
            }
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
