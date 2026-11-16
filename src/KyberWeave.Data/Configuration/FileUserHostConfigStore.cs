using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.FileSystem;
using YamlDotNet.Core;

namespace KyberWeave.Data.Configuration;

/// <summary>Keeps the per-user host config in <c>~/.kyber-weave/kyber-weave.yml</c> on the real file system.</summary>
public sealed class FileUserHostConfigStore : IUserHostConfigStore
{
    /// <inheritdoc />
    public string EnsureUserConfig(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        string path = UserHostConfigPath.Of(home);
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

    /// <inheritdoc />
    public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(targets);

        string path = UserHostConfigPath.Of(home);
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

    /// <inheritdoc />
    public void RemoveGlobalTarget(string home, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        string path = UserHostConfigPath.Of(home);
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

    /// <inheritdoc />
    public IReadOnlyList<string> ReadGlobalTargets(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        string path = UserHostConfigPath.Of(home);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            string yaml = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(yaml))
            {
                return [];
            }

            ArbiterYamlSection? section = KyberWeaveYamlParser.Parse(yaml).Arbiter;
            if (section?.Global?.Targets is null)
            {
                return [];
            }

            return [.. section.Global.Targets
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .Distinct(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is YamlException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
