using System.ComponentModel;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Arbiter.Mcp;

/// <summary>Builds the serve context over the hook's own engine, the host configuration and the user's credential stores.</summary>
public sealed class ArbiterServeContextFactory(
    ArbiterRootResolver roots,
    IContextualHookDecisionEngine engine,
    IArbiterHostConfig hostConfig,
    IArbiterUserSettings userSettings,
    ICredentialStore credentials,
    IProcessEnvironment process) : IArbiterServeContextFactory
{
    /// <inheritdoc/>
    public ArbiterServeContext Create(IReadOnlyList<string> args, TextWriter log)
    {
        string repoRoot = roots.Resolve(
            args,
            process.CurrentDirectory,
            process.GetVariable(ArbiterRootResolver.EnvironmentVariable));
        return new ArbiterServeContext(
            repoRoot,
            engine,
            log,
            root => hostConfig.Load(root),
            KeyResolved,
            EffectiveProvider);
    }

    // Presence only: the key is read from the store or the environment and discarded here.
    // Status reports the answer, never the value.
    private ArbiterProviderConfig EffectiveProvider(KyberWeaveConfig config) =>
        userSettings.ApplyTo(config.Arbiter.Provider, process.HomeDirectory);

    private bool KeyResolved(KyberWeaveConfig config)
    {
        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig effective = EffectiveProvider(config);
            string? userOverride = string.Equals(effective.Endpoint, repository.Endpoint, StringComparison.Ordinal)
                ? null
                : effective.Endpoint;
            return ArbiterKeyResolver.Resolve(effective.Endpoint, credentials, process.GetVariable, userOverride) is not null;
        }
        catch (Exception ex) when (FileFailure.Is(ex)
            || ex is YamlDotNet.Core.YamlException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }
}
