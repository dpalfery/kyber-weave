using System.Text.Json;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The hook host: one command, two kinds of hook. It chooses between dispatch gating
/// (the harness's dispatch tool) and the planning-path Read guard (implementation
/// specialists) by tool and caller class, through the harness adapter.
/// </summary>
/// <remarks>
/// The host writes the harness's decision document on stdout, and nothing else.
/// Logging goes to the <c>log</c> writer (stderr in production). The exit code is 0:
/// a block rides the decision document, so a failing gate never needs a failing
/// process. One top-level catch turns any exception into that harness's block
/// carrying <c>KW-ARB-HOOK-001</c>; on conductor dispatches the reason is an
/// escalation envelope with <c>ANSWER: error</c>.
/// </remarks>
public sealed class HookCommand
{
    /// <summary>The hook error code every host failure carries.</summary>
    public const string FailClosedCode = "KW-ARB-HOOK-001";

    private const string EvaluatingUnderProjectLine =
        "Arbiter global scope: evaluating under the project configuration.";

    private readonly HarnessAdapterRegistry _registry;
    private readonly Func<string, KyberWeaveConfig> _configLoader;
    private readonly Func<string> _newDecisionId;

    /// <summary>
    /// Creates a host over an adapter registry and a configuration loader.
    /// The loader is a port so tests can prove the fast path never invokes it.
    /// </summary>
    public HookCommand(
        HarnessAdapterRegistry registry,
        Func<string, KyberWeaveConfig> configLoader,
        Func<string>? newDecisionId = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configLoader);
        _registry = registry;
        _configLoader = configLoader;
        _newDecisionId = newDecisionId ?? (() => Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Runs one hook event. Returns 0 always: blocks ride stdout, and unknown-harness
    /// usage errors (which have no harness block shape) return 2 with stderr diagnostics.
    /// </summary>
    /// <remarks>
    /// The user-level hook (<see cref="HookScope.Global"/>) evaluates a project's dispatch under that
    /// project's configuration when the project enables the Arbiter, so both hooks decide the same way
    /// and write to the same project ledger. Each event is evaluated once: whichever hook claims the
    /// dispatch first (<see cref="DispatchClaim"/>) decides it, and the other defers or allows.
    /// </remarks>
    public int Run(
        string harness,
        string? caller,
        string stdin,
        TextWriter stdout,
        TextWriter log,
        HookScope scope = HookScope.Project,
        string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(log);

        if (!_registry.TryGet(harness, out IHarnessHookAdapter? adapter) || adapter is null)
        {
            log.WriteLine(
                $"Unknown harness '{harness}'. Known harnesses: " +
                $"{string.Join(", ", _registry.Tokens.Order(StringComparer.Ordinal))}. " +
                "Usage: kyber-weave-arbiter hook --harness <token> [--caller <agent>].");
            return 2;
        }

        try
        {
            JsonElement payload = ParsePayload(stdin);
            if (adapter.IsPassThrough(payload, caller))
            {
                return 0;
            }

            string? unconfigured = adapter.AnswerWithoutConfig(payload, caller);
            if (unconfigured is not null)
            {
                stdout.Write(unconfigured);
                return 0;
            }

            string repoRoot = RepoRootOf(adapter, payload);
            string home = Composition.ResolveHomeDirectory(homeDirectory);
            KyberWeaveConfig config;
            HookScope evaluationScope = scope;
            if (scope == HookScope.Global)
            {
                KyberWeaveConfig? projectConfig = LoadProjectConfig(repoRoot);
                if (projectConfig?.Arbiter.ExplicitlyEnabled == false)
                {
                    log.WriteLine("Arbiter global scope: deferred-to-project (project opts out: arbiter.enabled: false).");
                    return WriteDisabledAllow(adapter, stdout);
                }

                if (projectConfig is not null && projectConfig.Arbiter.Enabled)
                {
                    if (!string.Equals(adapter.HarnessToken, ClaudeHookAdapter.Token, StringComparison.Ordinal))
                    {
                        log.WriteLine(
                            $"Arbiter global scope: deferred-to-project ({adapter.HarnessToken}: no per-dispatch probe; " +
                            $"the project's own {adapter.HarnessToken} hooks decide).");
                        return WriteDisabledAllow(adapter, stdout);
                    }

                    if (!TakeUserLevelDispatch(adapter, payload, repoRoot, home, log))
                    {
                        return WriteDisabledAllow(adapter, stdout);
                    }

                    log.WriteLine(EvaluatingUnderProjectLine);
                    config = projectConfig;
                    evaluationScope = HookScope.Project;
                }
                else
                {
                    log.WriteLine("Arbiter global scope: evaluating with the user config.");
                    config = Composition.LoadHostConfig(repoRoot, scope, home);
                }
            }
            else
            {
                config = _configLoader(repoRoot);
            }

            if (!config.Arbiter.Enabled)
            {
                log.WriteLine("Arbiter is disabled (arbiter.enabled: false): allowing without evaluation.");
                // A harness whose protocol has no silent answer (Antigravity writes {}
                // for every event) still receives its allow document here.
                return WriteDisabledAllow(adapter, stdout);
            }

            if (scope == HookScope.Project && TryYieldToGlobalClaim(adapter, payload, repoRoot, log))
            {
                return WriteDisabledAllow(adapter, stdout);
            }

            HookContext context = new(
                repoRoot,
                _newDecisionId,
                log,
                adapter.ObservesReturns,
                home,
                evaluationScope,
                adapter.ModelOverride);
            string output = adapter.Handle(payload, stdin, caller, config, context);
            if (output.Length > 0)
            {
                stdout.Write(output);
            }

            return 0;
        }
        catch (Exception ex)
        {
            string block;
            try
            {
                block = adapter.RenderFailClosed(OneLine(ex.Message), caller, stdin, _newDecisionId());
            }
            catch (Exception)
            {
                // The block renderer never throws by contract; this is the last resort
                // so a host failure still fails closed when the renderer itself breaks.
                block = LastResortBlock.For(harness);
            }

            stdout.Write(block);
            return 0;
        }
    }

    private static JsonElement ParsePayload(string stdin)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdin);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Malformed hook event JSON: {OneLine(ex.Message)}", ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidOperationException("Malformed hook event: expected a JSON object on stdin.");
        }

        // The document owns the element's buffers; cloning detaches the payload so the
        // document can be disposed before evaluation. Hook payloads are small.
        JsonElement payload = document.RootElement.Clone();
        document.Dispose();
        return payload;
    }

    // The adapter's own payload root wins: Antigravity carries the workspace in
    // workspacePaths[0] ([F10]) and has no cwd, so without this the configuration
    // loads from the process directory. The payload's cwd is the Claude-family
    // fallback, and the process directory is the last resort.
    private static string RepoRootOf(IHarnessHookAdapter adapter, JsonElement payload) =>
        adapter.RepoRootOf(payload)
        ?? (payload.TryGetProperty("cwd", out JsonElement cwd)
            && cwd.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cwd.GetString())
                ? cwd.GetString()!
                : Directory.GetCurrentDirectory());

    // Null when the project has no config file. Throws when the file exists but does not load, so the
    // user-level hook fails closed rather than guessing which configuration applies.
    private static KyberWeaveConfig? LoadProjectConfig(string repoRoot)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(repoRoot);
        if (!loaded.Success)
        {
            throw new InvalidOperationException(
                $"{FailClosedCode}: failed to load project config '{loaded.ConfigPath}': {OneLine(loaded.Error ?? "unknown error")}");
        }

        return loaded.Config;
    }

    /// <summary>
    /// Decides whether the user-level hook evaluates a Claude dispatch for a project that enables the
    /// Arbiter. Returns false when the dispatch is covered: a project hook has claimed it, or one may
    /// still claim it and the claim window closed on it. Logs the reason either way.
    /// </summary>
    private static bool TakeUserLevelDispatch(
        IHarnessHookAdapter adapter,
        JsonElement payload,
        string repoRoot,
        string home,
        TextWriter log)
    {
        IReadOnlyList<string>? candidates = adapter.CandidateSources(payload, repoRoot, home);
        DispatchClaimKey? claim = adapter.ClaimKey(payload);

        // No project hook can fire on this event: the user-level hook is the only gate.
        if (candidates is null || candidates.Count == 0)
        {
            if (claim is null)
            {
                return true;
            }

            ClaimResult taken = TryClaimOrReport(repoRoot, claim, DispatchClaim.GlobalOwner, log);
            if (taken == ClaimResult.Held)
            {
                log.WriteLine($"Arbiter global scope: deferred-to-project (claimed by {OwnerOf(repoRoot, claim, log)}).");
                return false;
            }

            return true;
        }

        // Without a tool use id there is no claim to wait for, so the candidates decide alone.
        if (claim is null)
        {
            log.WriteLine(
                $"Arbiter global scope: deferred-to-project (project hooks may cover it: {string.Join(", ", candidates)}).");
            return false;
        }

        // A project hook that can fire will claim within the window; a claim means it did.
        if (DispatchClaim.WaitForClaim(repoRoot, claim))
        {
            log.WriteLine($"Arbiter global scope: deferred-to-project (claimed by {candidates[0]}).");
            return false;
        }

        if (TryClaimOrReport(repoRoot, claim, DispatchClaim.GlobalOwner, log) == ClaimResult.Held)
        {
            // A project hook claimed it after the window closed: the same deferral.
            log.WriteLine($"Arbiter global scope: deferred-to-project (claimed by {OwnerOf(repoRoot, claim, log)}).");
            return false;
        }

        log.WriteLine($"Arbiter global scope: candidate hooks did not fire: {string.Join(", ", candidates)}.");
        return true;
    }

    /// <summary>
    /// The project-scope half of the one-evaluation rule: when the user-level hook already claimed this
    /// dispatch, the project hook allows it without evaluating. Otherwise it takes the project claim (a
    /// re-run of a project hook still evaluates, as it always did) and returns false.
    /// </summary>
    private static bool TryYieldToGlobalClaim(IHarnessHookAdapter adapter, JsonElement payload, string repoRoot, TextWriter log)
    {
        DispatchClaimKey? claim = adapter.ClaimKey(payload);
        if (claim is null || TryClaimOrReport(repoRoot, claim, DispatchClaim.ProjectOwner, log) != ClaimResult.Held)
        {
            return false;
        }

        if (!string.Equals(OwnerOf(repoRoot, claim, log), DispatchClaim.GlobalOwner, StringComparison.Ordinal))
        {
            return false;
        }

        log.WriteLine("Arbiter: dispatch already claimed by the global hook; not evaluated twice.");
        return true;
    }

    // A claim store that cannot be written must not stop the gate. The dispatch is still evaluated, so
    // at worst both hooks decide it; the log says the claim was not recorded.
    private static ClaimResult TryClaimOrReport(string repoRoot, DispatchClaimKey claim, string owner, TextWriter log)
    {
        try
        {
            return DispatchClaim.TryClaim(repoRoot, claim, owner) ? ClaimResult.Created : ClaimResult.Held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Arbiter: could not record the dispatch claim ({OneLine(ex.Message)}); evaluating without it.");
            return ClaimResult.Unavailable;
        }
    }

    private static string OwnerOf(string repoRoot, DispatchClaimKey claim, TextWriter log)
    {
        try
        {
            string? owner = DispatchClaim.ReadOwner(repoRoot, claim);
            return string.IsNullOrWhiteSpace(owner) ? "another hook" : owner;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Arbiter: could not read the dispatch claim ({OneLine(ex.Message)}).");
            return "another hook";
        }
    }

    private enum ClaimResult
    {
        /// <summary>This hook created the claim and owns the dispatch.</summary>
        Created,

        /// <summary>Another hook holds the claim.</summary>
        Held,

        /// <summary>The claim store could not be written, so the dispatch is evaluated without a claim.</summary>
        Unavailable,
    }

    private static int WriteDisabledAllow(IHarnessHookAdapter adapter, TextWriter stdout)
    {
        string? disabledAllow = adapter.DisabledAllowDocument;
        if (disabledAllow is not null)
        {
            stdout.Write(disabledAllow);
        }

        return 0;
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
}
