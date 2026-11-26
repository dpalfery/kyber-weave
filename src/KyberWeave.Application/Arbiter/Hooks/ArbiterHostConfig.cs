using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;
using YamlDotNet.Core;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>Loads the configuration the Arbiter hook host decides under, through the configuration loader and file ports.</summary>
/// <param name="loader">Loads a repository's host configuration.</param>
/// <param name="files">Reads the user config and answers whether a project's config file exists.</param>
/// <param name="environment">The operator's home a <c>--home</c> value resolves against.</param>
public sealed class ArbiterHostConfig(IHostConfigLoader loader, IFileSystem files, IProcessEnvironment environment) : IArbiterHostConfig
{
    /// <inheritdoc/>
    public KyberWeaveConfig Load(string repoRoot, HookScope scope = HookScope.Project, string? homeDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        if (scope == HookScope.Global)
        {
            HostConfigLoadResult project = loader.TryLoad(repoRoot);
            if (!project.Success)
            {
                throw new InvalidOperationException(
                    $"{HookCommand.FailClosedCode}: failed to load project config '{project.ConfigPath}': {OneLine(project.Error ?? "unknown error")}");
            }

            if (project.Config is not null && project.Config.Arbiter.Enabled)
            {
                return project.Config;
            }

            string effectiveHome = ResolveHomeDirectory(homeDirectory);
            string userConfigPath = UserHostConfigPath.Of(effectiveHome);
            if (!files.FileExists(userConfigPath))
            {
                throw new InvalidOperationException(
                    $"{HookCommand.FailClosedCode}: missing user config '{userConfigPath}' for global scope. " +
                    $"Create '{UserHostConfigPath.Of(effectiveHome)}'.");
            }

            try
            {
                string yaml = files.ReadAllText(userConfigPath);
                KyberWeaveConfig config = new() { Arbiter = ArbiterConfigLoader.LoadUserConfig(yaml) };
                return config;
            }
            catch (Exception ex) when (FileFailure.Is(ex) || ex is YamlException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"{HookCommand.FailClosedCode}: malformed user config '{userConfigPath}': {OneLine(ex.Message)}",
                    ex);
            }
        }

        // Mirrors the lookup in the configuration loader: the default directory path, then the legacy root path.
        // TryLoad alone cannot report "missing" because it answers ProductDefaults when no file exists.
        string defaultPath = Path.Combine(repoRoot, UserStatePaths.DirectoryName, "kyber-weave.yml");
        string legacyPath = Path.Combine(repoRoot, "kyber-weave.yml");
        if (!files.FileExists(defaultPath) && !files.FileExists(legacyPath))
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: no .kyber-weave/kyber-weave.yml found under " +
                $"'{repoRoot}'. Run kyber-weave docs init to create host configuration.");
        }

        HostConfigLoadResult loaded = loader.TryLoad(repoRoot);
        if (!loaded.Success || loaded.Config is null)
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: failed to load " +
                $"'{loaded.ConfigPath ?? defaultPath}': {loaded.Error ?? "unknown error"}.");
        }

        return loaded.Config;
    }

    /// <inheritdoc/>
    public KyberWeaveConfig? LoadProject(string repoRoot)
    {
        HostConfigLoadResult loaded = loader.TryLoad(repoRoot);
        if (!loaded.Success)
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: failed to load project config '{loaded.ConfigPath}': {OneLine(loaded.Error ?? "unknown error")}");
        }

        return loaded.Config;
    }

    /// <inheritdoc/>
    public string ResolveHomeDirectory(string? homeDirectory)
    {
        if (string.IsNullOrWhiteSpace(homeDirectory) || string.Equals(homeDirectory, "~", StringComparison.Ordinal))
        {
            return environment.HomeDirectory;
        }

        if (homeDirectory.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(environment.HomeDirectory, homeDirectory[2..]);
        }

        return homeDirectory;
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
}
