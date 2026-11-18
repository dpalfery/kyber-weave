using System.Text.Json;
using KyberWeave.Arbiter.Hooks.Adapters;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.FileSystem;

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
/// process. One top-level catch turns any exception into an allow: the host logs
/// <c>KW-ARB-HOOK-001</c> with the reason and writes the harness's allow document,
/// because a host that cannot decide must not block the dispatch (ADR 0072).
/// </remarks>
public sealed class HookCommand : IArbiterHookHost
{
    /// <summary>The hook error code every host failure carries.</summary>
    public const string FailClosedCode = "KW-ARB-HOOK-001";

    private const string EvaluatingUnderProjectLine =
        "Arbiter global scope: evaluating under the project configuration.";

    private readonly HarnessAdapterRegistry _registry;
    private readonly IArbiterHostConfig _hostConfig;
    private readonly IDispatchClaimStore _claims;
    private readonly IClaudeHookCoverage _coverage;
    private readonly IProcessEnvironment _environment;
    private readonly Func<string> _newDecisionId;
    private readonly ITaskPromptServiceFactory? _promptFactory;

    /// <summary>
    /// Creates a host over an adapter registry and the ports it decides through. The configuration loader is a port so
    /// tests can prove the fast path never invokes it.
    /// </summary>
    /// <param name="registry">The harness adapters.</param>
    /// <param name="hostConfig">Loads the configuration an event is decided under.</param>
    /// <param name="claims">The one-evaluation-per-dispatch claim store.</param>
    /// <param name="coverage">Reads the hook sources a project declares, so the user-level hook can tell whether a project hook covers a dispatch.</param>
    /// <param name="environment">The process directory a payload with no <c>cwd</c> falls back to.</param>
    /// <param name="ids">The identifier seam; its GUID, as 32 hex digits, is the decision id when <paramref name="newDecisionId"/> is null.</param>
    /// <param name="newDecisionId">Builds a decision id; null uses <paramref name="ids"/>.</param>
    /// <param name="prompts">Builds the task prompt a dispatch's identity line is replaced with; null installs no
    /// prompt service, so no dispatch is expanded.</param>
    public HookCommand(
        HarnessAdapterRegistry registry,
        IArbiterHostConfig hostConfig,
        IDispatchClaimStore claims,
        IClaudeHookCoverage coverage,
        IProcessEnvironment environment,
        IIdGenerator ids,
        Func<string>? newDecisionId = null,
        ITaskPromptServiceFactory? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(hostConfig);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(ids);
        _registry = registry;
        _hostConfig = hostConfig;
        _claims = claims;
        _coverage = coverage;
        _environment = environment;
        _newDecisionId = newDecisionId ?? (() => ids.NewGuid().ToString("N"));
        _promptFactory = prompts;
    }

    /// <summary>
    /// Runs one hook event. Returns 0 for every event: blocks ride stdout. An unknown
    /// harness token is a usage error with no harness document to write; it returns 1 with
    /// stderr diagnostics, which Claude and Copilot treat as a non-blocking hook error.
    /// </summary>
    /// <remarks>
    /// The user-level hook (<see cref="HookScope.Global"/>) evaluates a project's dispatch under that
    /// project's configuration when the project enables the Arbiter, so both hooks decide the same way
    /// and write to the same project ledger. Each event is evaluated once: whichever hook claims the
    /// dispatch first (<see cref="IDispatchClaimStore"/>) decides it, and the other defers or allows.
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
            return 1;
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
            string home = _hostConfig.ResolveHomeDirectory(homeDirectory);
            KyberWeaveConfig config;
            HookScope evaluationScope = scope;
            if (scope == HookScope.Global)
            {
                KyberWeaveConfig? projectConfig = _hostConfig.LoadProject(repoRoot);
                if (projectConfig?.Arbiter.ExplicitlyEnabled == false)
                {
                    log.WriteLine("Arbiter global scope: deferred-to-project (project opts out: arbiter.enabled: false).");
                    // The Arbiter is off, but prompt insertion is not an Arbiter action: the dispatch still gets its
                    // prompt. A project hook may insert it too; a dispatch already carrying the prompt is left alone.
                    return AllowWithoutEvaluation(
                        adapter,
                        payload,
                        caller,
                        NewContext(adapter, repoRoot, home, HookScope.Project, projectConfig!, log),
                        stdout);
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
                    config = _hostConfig.Load(repoRoot, scope, home);
                }
            }
            else
            {
                config = _hostConfig.Load(repoRoot);
            }

            if (!config.Arbiter.Enabled)
            {
                log.WriteLine("Arbiter is disabled (arbiter.enabled: false): allowing without evaluation.");
                // Prompt insertion is not an Arbiter action: a host with the Arbiter off still expands the dispatch.
                // A harness whose protocol has no silent answer (Antigravity writes {} for every event) still
                // receives its allow document here.
                return AllowWithoutEvaluation(
                    adapter,
                    payload,
                    caller,
                    NewContext(adapter, repoRoot, home, evaluationScope, config, log),
                    stdout);
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
                adapter.ModelOverride,
                BuildDispatchPrompt: PromptExpander(repoRoot, config));
            string output = adapter.Handle(payload, stdin, caller, config, context);
            if (output.Length > 0)
            {
                stdout.Write(output);
            }

            return 0;
        }
        catch (Exception ex)
        {
            // Fail open: the host could not reach a decision, so the dispatch continues and the
            // failure is logged with its code. Only a positive finding may block (ADR 0072).
            log.WriteLine(
                $"{FailClosedCode}: hook host failed ({_newDecisionId()}) on '{harness}': {OneLine(ex.Message)}. " +
                "The dispatch continues without an Arbiter decision.");
            return WriteDisabledAllow(adapter, stdout);
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
    private string RepoRootOf(IHarnessHookAdapter adapter, JsonElement payload) =>
        adapter.RepoRootOf(payload)
        ?? (payload.TryGetProperty("cwd", out JsonElement cwd)
            && cwd.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(cwd.GetString())
                ? cwd.GetString()!
                : _environment.CurrentDirectory);

    /// <summary>
    /// Decides whether the user-level hook evaluates a Claude dispatch for a project that enables the
    /// Arbiter. Returns false when the dispatch is covered: a project hook has claimed it, or one may
    /// still claim it and the claim window closed on it. Logs the reason either way.
    /// </summary>
    private bool TakeUserLevelDispatch(
        IHarnessHookAdapter adapter,
        JsonElement payload,
        string repoRoot,
        string home,
        TextWriter log)
    {
        HookCoverageQuery? query = adapter.CoverageQuery(payload);
        IReadOnlyList<string>? candidates = query is null
            ? null
            : _coverage.CandidateSources(repoRoot, home, query.AgentType, query.EventName, query.ToolName);
        DispatchClaimKey? claim = adapter.ClaimKey(payload);

        // No project hook can fire on this event: the user-level hook is the only gate.
        if (candidates is null || candidates.Count == 0)
        {
            if (claim is null)
            {
                return true;
            }

            ClaimResult taken = TryClaimOrReport(repoRoot, claim, DispatchClaimOwners.Global, log);
            if (taken == ClaimResult.Held)
            {
                log.WriteLine($"Arbiter global scope: deferred-to-project (claimed by {OwnerOf(repoRoot, claim, log)}).");
                return false;
            }

            return true;
        }

        // Without a tool use id there is no claim to wait for or to take. A candidate may be a skill that
        // was never invoked, and deferring to it would leave the dispatch with no gate at all, so this hook
        // evaluates: at worst a project hook decides it too.
        if (claim is null)
        {
            log.WriteLine(
                $"Arbiter global scope: no tool use id to coordinate on; evaluating although project hooks may cover it: {string.Join(", ", candidates)}.");
            return true;
        }

        // A project hook that can fire will claim within the window; a claim means it did.
        if (_claims.WaitForClaim(repoRoot, claim))
        {
            log.WriteLine(
                $"Arbiter global scope: deferred-to-project (claimed by {OwnerOf(repoRoot, claim, log)}; project hooks that can fire: {string.Join(", ", candidates)}).");
            return false;
        }

        if (TryClaimOrReport(repoRoot, claim, DispatchClaimOwners.Global, log) == ClaimResult.Held)
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
    private bool TryYieldToGlobalClaim(IHarnessHookAdapter adapter, JsonElement payload, string repoRoot, TextWriter log)
    {
        DispatchClaimKey? claim = adapter.ClaimKey(payload);
        if (claim is null || TryClaimOrReport(repoRoot, claim, DispatchClaimOwners.Project, log) != ClaimResult.Held)
        {
            return false;
        }

        if (!string.Equals(OwnerOf(repoRoot, claim, log), DispatchClaimOwners.Global, StringComparison.Ordinal))
        {
            return false;
        }

        log.WriteLine("Arbiter: dispatch already claimed by the global hook; not evaluated twice.");
        return true;
    }

    // A claim store that cannot be written must not stop the gate. The dispatch is still evaluated, so
    // at worst both hooks decide it; the log says the claim was not recorded.
    private ClaimResult TryClaimOrReport(string repoRoot, DispatchClaimKey claim, string owner, TextWriter log)
    {
        try
        {
            return _claims.TryClaim(repoRoot, claim, owner) ? ClaimResult.Created : ClaimResult.Held;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            log.WriteLine($"Arbiter: could not record the dispatch claim ({OneLine(ex.Message)}); evaluating without it.");
            return ClaimResult.Unavailable;
        }
    }

    private string OwnerOf(string repoRoot, DispatchClaimKey claim, TextWriter log)
    {
        try
        {
            string? owner = _claims.ReadOwner(repoRoot, claim);
            return string.IsNullOrWhiteSpace(owner) ? "another hook" : owner;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
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

    /// <summary>The context an unevaluated event is answered under, carrying the prompt service when one is installed.</summary>
    private HookContext NewContext(
        IHarnessHookAdapter adapter,
        string repoRoot,
        string home,
        HookScope scope,
        KyberWeaveConfig config,
        TextWriter log) =>
        new(
            repoRoot,
            _newDecisionId,
            log,
            adapter.ObservesReturns,
            home,
            scope,
            adapter.ModelOverride,
            BuildDispatchPrompt: PromptExpander(repoRoot, config));

    /// <summary>The prompt builder this host passes to the adapters, or null when no prompt service was installed.</summary>
    /// <remarks>
    /// The service is built once per hook process and reads the checkout's documentation, so it is created on the
    /// first dispatch that needs it rather than for every event.
    /// </remarks>
    private Func<DispatchPromptQuery, DispatchPromptExpansion>? PromptExpander(string repoRoot, KyberWeaveConfig config)
    {
        if (_promptFactory is null)
        {
            return null;
        }

        ITaskPromptService? service = null;
        return query =>
        {
            service ??= _promptFactory.ForRepository(repoRoot, config);
            TaskPromptResult result = service.Build(
                new TaskPromptRequest(
                    repoRoot,
                    query.TaskId,
                    query.PlanFile,
                    Agent: null,
                    query.PriorResultsPath,
                    Technology: query.Technology,
                    query.FailedAttempts,
                    RetryText: query.RetryText),
                config);
            return new DispatchPromptExpansion(result.Built, result.Prompt, result.Refusal);
        };
    }

    /// <summary>
    /// Answers an event this hook does not evaluate: with the dispatch expanded when it names a task, else with the
    /// harness's own allow document.
    /// </summary>
    private static int AllowWithoutEvaluation(
        IHarnessHookAdapter adapter,
        JsonElement payload,
        string? caller,
        HookContext context,
        TextWriter stdout)
    {
        string? insertion = adapter.RenderPromptInsertion(payload, caller, context);
        if (insertion is not null)
        {
            stdout.Write(insertion);
            return 0;
        }

        return WriteDisabledAllow(adapter, stdout);
    }

    private static string OneLine(string message) =>
        message.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ');
}
