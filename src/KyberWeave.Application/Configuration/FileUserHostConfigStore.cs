using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using YamlDotNet.Core;

namespace KyberWeave.Data.Configuration;

/// <summary>Keeps the per-user host config in <c>~/.kyber-weave/kyber-weave.yml</c> through the file-system seam.</summary>
/// <param name="files">The file system the config lives on.</param>
public sealed class FileUserHostConfigStore(IWritableFileSystem files) : IUserHostConfigStore
{
    /// <inheritdoc />
    public string EnsureUserConfig(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        string path = UserHostConfigPath.Of(home);
        files.CreateDirectory(Path.GetDirectoryName(path)!);

        string yaml = files.FileExists(path)
            ? files.ReadAllText(path)
            : string.Empty;

        string updated = HostConfigYaml.WithArbiterEnabled(yaml);
        if (!files.FileExists(path) || !string.Equals(yaml, updated, StringComparison.Ordinal))
        {
            files.WriteAllTextAtomic(path, updated);
        }

        return updated;
    }

    /// <inheritdoc />
    public void WriteGlobalTargets(string home, IReadOnlyList<string> targets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(targets);

        string path = UserHostConfigPath.Of(home);
        files.CreateDirectory(Path.GetDirectoryName(path)!);

        string yaml = files.FileExists(path)
            ? files.ReadAllText(path)
            : EnsureUserConfig(home);

        string updated = HostConfigYaml.WithArbiterGlobalTargets(yaml, targets);
        if (!string.Equals(yaml, updated, StringComparison.Ordinal))
        {
            files.WriteAllTextAtomic(path, updated);
        }
    }

    /// <inheritdoc />
    public void RemoveGlobalTarget(string home, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        string path = UserHostConfigPath.Of(home);
        if (!files.FileExists(path))
        {
            return;
        }

        string yaml = files.ReadAllText(path);
        string updated = HostConfigYaml.WithoutArbiterGlobalTarget(yaml, target);
        if (!string.Equals(yaml, updated, StringComparison.Ordinal))
        {
            files.WriteAllTextAtomic(path, updated);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadGlobalTargets(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);

        string path = UserHostConfigPath.Of(home);
        if (!files.FileExists(path))
        {
            return [];
        }

        try
        {
            string yaml = files.ReadAllText(path);
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
