using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Rules;
using YamlDotNet.Core;

namespace KyberWeave.Data.Arbiter;

/// <summary>Reads the per-user Arbiter override file and applies its provider fields.</summary>
/// <param name="fileSystem">The file system the override is read from.</param>
public sealed class FileArbiterUserSettings(IFileSystem fileSystem) : IArbiterUserSettings
{
    /// <inheritdoc />
    public ArbiterProviderConfig ApplyTo(ArbiterProviderConfig repository, string homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        string path = ArbiterUserSettings.GetPath(homeDirectory);
        if (!fileSystem.FileExists(path))
        {
            return ArbiterConfigLoader.ApplyUserProviderOverride(repository, null);
        }

        string yaml;
        try
        {
            yaml = fileSystem.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new YamlException(
                $"[{RuleValidator.MalformedSection}] The user override '{path}' could not be read.",
                exception);
        }

        return ArbiterConfigLoader.ApplyUserProviderOverride(repository, ArbiterUserSettings.ParseProvider(yaml, path));
    }
}
