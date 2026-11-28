using System.ComponentModel;
using System.Text.Json.Serialization;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Mcp;

/// <summary>The collaborators one serve process answers with. The composition root builds it; tests inject fakes.</summary>
/// <param name="RepoRoot">The resolved root every response names.</param>
/// <param name="Engine">The hook's own decision engine, so a served decision is the hook's decision.</param>
/// <param name="Log">Diagnostics sink: stderr in production, never stdout.</param>
/// <param name="LoadConfig">Loads the host configuration for the root; throws when it is missing or malformed.</param>
/// <param name="KeyResolvedAsync">Whether a key resolves for the effective provider, without blocking a thread. It reports presence only, never the key.</param>
/// <param name="EffectiveProvider">Applies the user override to the repository provider, the provider the hook resolves its key for. Null means the repository provider is the effective one.</param>
public sealed record ArbiterServeContext(
    string RepoRoot,
    IContextualHookDecisionEngine Engine,
    TextWriter Log,
    Func<string, KyberWeaveConfig> LoadConfig,
    Func<KyberWeaveConfig, CancellationToken, Task<bool>> KeyResolvedAsync,
    Func<KyberWeaveConfig, ArbiterProviderConfig>? EffectiveProvider = null);
