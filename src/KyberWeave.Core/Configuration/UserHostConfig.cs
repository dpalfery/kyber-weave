using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Parsing;

namespace KyberWeave.Core.Configuration;

/// <summary>
/// User-level host config path and global target ledger.
/// </summary>
public static class UserHostConfig
{
    public static string GetPath(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        return Path.Combine(home, ".kyber-weave", "kyber-weave.yml");
    }

    public static string EnsureUserConfig(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        string path = GetPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string yaml = File.Exists(path)
            ? File.ReadAllText(path)
            : string.Empty;

        string updated = HostConfigYaml.WithArbiterEnabled(yaml);
        if (!File.Exists(path) || !string.Equals(yaml, updated, StringComparison.Ordinal))
        {
            AtomicFile.WriteAllText(path, updated);
        }

        return updated;
    }

    public static void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(targets);

        string path = GetPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string yaml = File.Exists(path)
            ? File.ReadAllText(path)
            : EnsureUserConfig(home);

        string updated = HostConfigYaml.WithArbiterGlobalTargets(yaml, targets);
        if (!string.Equals(yaml, updated, StringComparison.Ordinal))
        {
            AtomicFile.WriteAllText(path, updated);
        }
    }

    public static void RemoveGlobalTarget(string home, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        string path = GetPath(home);
        if (!File.Exists(path))
        {
            return;
        }

        string yaml = File.ReadAllText(path);
        string updated = HostConfigYaml.WithoutArbiterGlobalTarget(yaml, target);
        if (!string.Equals(yaml, updated, StringComparison.Ordinal))
        {
            AtomicFile.WriteAllText(path, updated);
        }
    }

    public static IReadOnlyList<string> ReadGlobalTargets(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        string path = GetPath(home);
        if (!File.Exists(path))
        {
            return [];
        }

        string yaml = File.ReadAllText(path);
        ArbiterYamlSection? section = KyberWeaveYamlParser.Parse(yaml).Arbiter;
        if (section?.Global?.Targets is null)
        {
            return [];
        }

        return [.. section.Global.Targets
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Distinct(StringComparer.Ordinal)];
    }
}
