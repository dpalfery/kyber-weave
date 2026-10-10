using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using YamlDotNet.Core;

namespace KyberWeave.Arbiter.Hooks;

/// <summary>
/// The production hook decision engine: runs <see cref="ArbiterEvaluator"/> over the
/// classified event and maps the result onto the engine port the adapters render.
/// </summary>
/// <remarks>
/// Every collaborator is built per evaluation from the loaded host configuration:
/// the provider (<c>none</c> or <c>systemone</c>), the origin-bound key resolver over
/// the OS credential stores, the ledger and decision log under
/// <c>artifacts/arbiter/</c>, real git facts and the plan reader. The user override
/// (<c>~/.config/kyber-weave/arbiter.yml</c>) applies field by field to the provider,
/// as in every other host. A non-allow outcome renders the escalation envelope on
/// conductor and investigate triggers, the post-return block, or the review note;
/// an error result — the evaluator throws rather than allows — blocks with
/// <c>KW-ARB-HOOK-001</c>. Nothing but the returned outcome ever reaches stdout:
/// diagnostics go to <see cref="HookContext.Log"/> (stderr in production).
/// Evaluation is synchronous at this boundary because the adapters are; the wait
/// stays inside the harness's per-hook timeout.
/// </remarks>
public sealed class ArbiterHookDecisionEngine : IContextualHookDecisionEngine
{
    private readonly Func<KyberWeaveConfig, IArbiterProvider> _providerFactory;
    private readonly Func<string, IArbiterGitFacts> _gitFactsFactory;
    private readonly Func<string, IArbiterPlanReader> _planReaderFactory;
    private readonly Func<string> _homeDirectory;
    private readonly Func<ICredentialStore> _credentialStoreFactory;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Creates an engine. Every factory is injectable so tests run the real
    /// evaluation end to end with a fake provider against a temp repo; omitted
    /// factories are the production ones.
    /// </summary>
    public ArbiterHookDecisionEngine(
        Func<KyberWeaveConfig, IArbiterProvider>? providerFactory = null,
        Func<string, IArbiterGitFacts>? gitFactsFactory = null,
        Func<string, IArbiterPlanReader>? planReaderFactory = null,
        Func<string>? homeDirectory = null,
        TimeProvider? clock = null,
        Func<ICredentialStore>? credentialStoreFactory = null,
        Func<string, string?>? environment = null)
    {
        _homeDirectory = homeDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Func<string> home = _homeDirectory;
        _providerFactory = providerFactory ?? (config => CreateProvider(ApplyUserProvider(config, home)));
        _gitFactsFactory = gitFactsFactory ?? (root => new HookGitFacts(root));
        _planReaderFactory = planReaderFactory ?? (root => new HookPlanReader(root));
        _clock = clock ?? TimeProvider.System;
        _credentialStoreFactory = credentialStoreFactory ?? CreateCredentialStore;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <inheritdoc/>
    public HookOutcome DecidePreDispatch(string caller, string? target, string prompt, KyberWeaveConfig config) =>
        DecidePreDispatch(
            "claude",
            caller,
            target,
            prompt,
            config,
            new HookContext(Directory.GetCurrentDirectory(), () => Guid.NewGuid().ToString("N"), TextWriter.Null));

    /// <inheritdoc/>
    public HookOutcome DecidePostDispatch(string caller, string? target, string toolOutput, KyberWeaveConfig config) =>
        DecidePostDispatch(
            "claude",
            caller,
            target,
            string.Empty,
            toolOutput,
            config,
            new HookContext(Directory.GetCurrentDirectory(), () => Guid.NewGuid().ToString("N"), TextWriter.Null));

    /// <inheritdoc/>
    public HookOutcome DecidePreDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        return Decide(harness, caller, target, prompt, null, isPost: false, config, context, toolCallId, sessionId);
    }

    /// <inheritdoc/>
    public HookOutcome DecidePostDispatch(
        string harness,
        string caller,
        string? target,
        string prompt,
        string toolOutput,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId = null,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(toolOutput);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        return Decide(harness, caller, target, prompt, toolOutput, isPost: true, config, context, toolCallId, sessionId);
    }

    private HookOutcome Decide(
        string harness,
        string caller,
        string? target,
        string prompt,
        string? toolOutput,
        bool isPost,
        KyberWeaveConfig config,
        HookContext context,
        string? toolCallId,
        string? sessionId)
    {
        string? resolved = string.IsNullOrWhiteSpace(caller) || IsUnidentified(caller) ? null : caller;

        // A serve event arrives with no harness hook: the whole event is the MCP
        // client's assertion (design 'Fact labels': derived vs asserted, R7), so a
        // caller it names records as asserted. A hook event keeps the harness label —
        // there the caller is what the harness payload itself reported.
        bool asserted = resolved is not null
            && string.Equals(harness, ArbiterServeToken.Harness, StringComparison.Ordinal);
        string repoRoot = context.RepoRoot;
        string arbiterDirectory = Path.Combine(repoRoot, "artifacts", "arbiter");
        KyberWeaveConfig effective;
        try
        {
            effective = ApplyUserProvider(config, _homeDirectory);
        }
        catch (InvalidOperationException ex)
        {
            // A malformed user override fails closed like a malformed host config.
            return BlockWithError(harness, resolved, ex.Message, context.NewDecisionId(), isPost);
        }

        IArbiterProvider provider = _providerFactory(effective);
        IDisposable? owned = provider as IDisposable;
        try
        {
            ArbiterEvaluator evaluator = new(
                provider,
                KeyResolverFor(effective),
                new InFlightLedger(arbiterDirectory),
                new DecisionLog(arbiterDirectory),
                _gitFactsFactory(repoRoot),
                _planReaderFactory(repoRoot),
                _clock);
            ArbiterEvent ev = new()
            {
                Harness = harness,
                Phase = isPost ? "post" : "pre",
                Caller = resolved,
                CallerSource = resolved is null
                    ? ArbiterCallerSources.None
                    : asserted ? ArbiterCallerSources.Asserted : ArbiterCallerSources.Harness,
                HarnessCaller = asserted ? null : resolved,
                RenderedCaller = asserted ? null : resolved,
                AssertedCaller = asserted ? resolved : null,
                Target = target,
                Prompt = prompt,
                ToolCallId = toolCallId,
                Session = sessionId,
                Cwd = repoRoot,
                ObservesReturns = context.ObservesReturns,
                ToolOutput = toolOutput,
                IsDispatch = true,
            };
            ArbiterEvaluationResult result = evaluator.EvaluateAsync(
                ev, effective, new ArbiterEvaluatorOptions(Source: ArbiterSources.Hook))
                .GetAwaiter().GetResult();
            return MapResult(result, harness, resolved, prompt, effective, arbiterDirectory, context, isPost);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static HookOutcome MapResult(
        ArbiterEvaluationResult result,
        string harness,
        string? caller,
        string prompt,
        KyberWeaveConfig config,
        string arbiterDirectory,
        HookContext context,
        bool isPost)
    {
        if (result.IsError)
        {
            return BlockWithError(
                harness, caller, OneLine(result.ErrorMessage), context.NewDecisionId(), isPost);
        }

        if (string.Equals(result.Outcome, RuleEffects.Allow, StringComparison.Ordinal)
            || string.Equals(result.Outcome, RuleEffects.Applies, StringComparison.Ordinal)
            || string.Equals(result.Outcome, RuleEffects.NotApplicable, StringComparison.Ordinal))
        {
            return new HookOutcome(HookOutcomeKind.Allow);
        }

        ArbiterDecisionRecord? record = string.IsNullOrEmpty(result.LedgerId)
            ? null
            : new DecisionLog(arbiterDirectory).ReadAll()
                .LastOrDefault(decision => string.Equals(decision.LedgerId, result.LedgerId, StringComparison.Ordinal));
        if (record is null)
        {
            return BlockWithError(
                harness, caller, "the decision log holds no decision for this evaluation.",
                context.NewDecisionId(), isPost);
        }

        string reason = RenderReason(record, prompt, harness, caller, config);
        context.Log.WriteLine($"Arbiter {record.Trigger} {record.Outcome}: blocking dispatch to '{record.Target ?? "<none>"}'.");
        if (string.Equals(record.Outcome, RuleEffects.Annotate, StringComparison.Ordinal))
        {
            return isPost
                ? new HookOutcome(HookOutcomeKind.PostAnnotation, reason)
                : new HookOutcome(HookOutcomeKind.Deny, reason);
        }

        return isPost
            ? new HookOutcome(HookOutcomeKind.PostBlock, reason)
            : new HookOutcome(HookOutcomeKind.Deny, reason);
    }

    private static string RenderReason(
        ArbiterDecisionRecord record,
        string prompt,
        string harness,
        string? caller,
        KyberWeaveConfig config)
    {
        string callerText = $"{caller ?? "unidentified"} ({harness})";
        IReadOnlyDictionary<string, string> headers = HeaderBlock.Parse(prompt).Headers;

        if (string.Equals(record.Outcome, RuleEffects.Skip, StringComparison.Ordinal))
        {
            headers.TryGetValue("LENS", out string? lens);
            return new ArbiterSkipNote(
                lens ?? "unknown",
                RuleIdsOf(record),
                AnswerOf(record.Rules.Count > 0 ? record.Rules[0] : null, record),
                record.Id).Render();
        }

        if (string.Equals(record.Outcome, RuleEffects.Verify, StringComparison.Ordinal))
        {
            headers.TryGetValue("REFUTE", out string? refute);
            return new ArbiterVerifiedNote(
                refute ?? "unknown",
                RuleIdsOf(record),
                AnswerOf(record.Rules.Count > 0 ? record.Rules[0] : null, record),
                record.Id).Render();
        }

        if (string.Equals(record.Outcome, RuleEffects.Annotate, StringComparison.Ordinal))
        {
            return new ArbiterAnnotationNote(
                FindingOf(prompt, record),
                RuleIdsOf(record),
                AnswerOf(record.Rules.Count > 0 ? record.Rules[0] : null, record),
                record.Id).Render();
        }

        List<ArbiterEnvelopeEntry> entries = EntriesOf(record, config);
        return new ArbiterEscalationEnvelope(
            record.Trigger,
            callerText,
            record.Target,
            record.PlanFile,
            record.Task,
            entries,
            record.Id,
            record.Repeat).Render();
    }

    private static IReadOnlyList<string> RuleIdsOf(ArbiterDecisionRecord record) =>
        [.. record.Rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal)];

    private static ArbiterAnswerText AnswerOf(ArbiterDecisionRule? rule, ArbiterDecisionRecord record)
    {
        if (rule is null)
        {
            return ArbiterAnswerText.Undecidable(record.Provider.Status);
        }

        if (rule.Step == 0)
        {
            return ArbiterAnswerText.Step0(rule.Answer);
        }

        if (string.Equals(rule.Answer, RuleEngine.Undecidable, StringComparison.Ordinal))
        {
            return ArbiterAnswerText.Undecidable(record.Provider.Status);
        }

        // Decision records carry no step-1 scores, so a scored answer renders in
        // the bare step-1 form rather than with a fabricated probability.
        return new ArbiterAnswerText($"{rule.Answer} (step 1)");
    }

    private static List<ArbiterEnvelopeEntry> EntriesOf(ArbiterDecisionRecord record, KyberWeaveConfig config)
    {
        List<ArbiterDecisionRule> firing = [.. record.Rules.Where(rule =>
            !string.Equals(rule.Effect, RuleEffects.Allow, StringComparison.Ordinal)
            && !string.Equals(rule.Effect, RuleEffects.NotApplicable, StringComparison.Ordinal))];
        if (firing.Count == 0)
        {
            firing.AddRange(record.Rules);
        }

        Dictionary<string, string> questions = config.Arbiter.Rules
            .GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Question, StringComparer.Ordinal);
        return [.. firing.Select(rule => new ArbiterEnvelopeEntry(
            rule.Id,
            questions.TryGetValue(rule.Id, out string? question) ? question : "What did the rule decide?",
            AnswerOf(rule, record),
            string.IsNullOrWhiteSpace(rule.Evidence)
                ? $"rule {rule.Id} answered '{rule.Answer}' on {record.Trigger}"
                : rule.Evidence))];
    }

    private static string FindingOf(string prompt, ArbiterDecisionRecord record)
    {
        using StringReader reader = new(prompt);
        while (reader.ReadLine() is { } line)
        {
            const string prefix = "FINDING:";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                string finding = line[prefix.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(finding))
                {
                    return finding;
                }
            }
        }

        return record.Task ?? record.Trigger;
    }

    private static bool IsUnidentified(string caller) =>
        string.Equals(caller, "unidentified", StringComparison.Ordinal);

    private static string OneLine(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? "unknown error"
            : message.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');

    private static HookOutcome BlockWithError(
        string harness, string? caller, string? message, string decisionId, bool isPost)
    {
        string flat = OneLine(message);
        if (flat.StartsWith(HookCommand.FailClosedCode + ": ", StringComparison.Ordinal))
        {
            flat = flat.Substring((HookCommand.FailClosedCode + ": ").Length);
        }

        string trigger = string.Equals(caller, "conductor", StringComparison.Ordinal) ? "delegate" : "investigate";
        string rendered = new ArbiterEscalationEnvelope(
            trigger,
            $"{caller ?? "unidentified"} ({harness})",
            null,
            null,
            null,
            [new ArbiterEnvelopeEntry(
                HookCommand.FailClosedCode,
                "Did the hook host fail before evaluation completed?",
                ArbiterAnswerText.Error(HookCommand.FailClosedCode, flat),
                $"{HookCommand.FailClosedCode}: {flat}")],
            string.IsNullOrWhiteSpace(decisionId) ? "unknown" : decisionId,
            1).Render();
        return isPost
            ? new HookOutcome(HookOutcomeKind.PostBlock, rendered)
            : new HookOutcome(HookOutcomeKind.Deny, rendered);
    }

    private Func<Uri, string?> KeyResolverFor(KyberWeaveConfig config)
    {
        ICredentialStore store = _credentialStoreFactory();
        string? userOverrideEndpoint = UserOverrideEndpoint(config, _homeDirectory);
        return uri => ArbiterKeyResolver.Resolve(uri.ToString(), store, _environment, userOverrideEndpoint);
    }

    private static ICredentialStore CreateCredentialStore()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainCredentialStore(new ProcessRunnerCredentialProcessRunner());
        }

        return new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The handler's lifetime transfers to the returned provider, which the caller disposes after the evaluation.")]
    private IArbiterProvider CreateProvider(KyberWeaveConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Arbiter.Provider.Kind == ArbiterProviderKind.None)
        {
            return new NoneProvider();
        }

        if (!Uri.TryCreate(config.Arbiter.Provider.Endpoint, UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: arbiter.provider.endpoint " +
                $"'{config.Arbiter.Provider.Endpoint}' is not an absolute URL.");
        }

        Func<Uri, string?> keyResolver = KeyResolverFor(config);
        return new SystemOneClient(
            new HttpClientHandler(),
            endpoint,
            config.Arbiter.Provider.Model,
            config.Arbiter.Provider.TimeoutMs,
            keyResolver);
    }

    private static KyberWeaveConfig ApplyUserProvider(KyberWeaveConfig config, Func<string> homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(homeDirectory);
        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = ArbiterUserSettings.ApplyTo(repository, homeDirectory());
            if (merged.Equals(repository))
            {
                return config;
            }

            return new KyberWeaveConfig
            {
                Ontology = config.Ontology,
                Harness = config.Harness,
                DocsAnalysis = config.DocsAnalysis,
                Squad = config.Squad,
                ConfigReg = config.ConfigReg,
                Review = config.Review,
                Arbiter = config.Arbiter with { Provider = merged },
            };
        }
        catch (Exception exception) when (exception is YamlException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{HookCommand.FailClosedCode}: the user override " +
                $"'{ArbiterUserSettings.GetPath(homeDirectory())}' is malformed: {OneLine(exception.Message)}",
                exception);
        }
    }

    private static string? UserOverrideEndpoint(KyberWeaveConfig config, Func<string> homeDirectory)
    {
        try
        {
            ArbiterProviderConfig repository = config.Arbiter.Provider;
            ArbiterProviderConfig merged = ArbiterUserSettings.ApplyTo(repository, homeDirectory());
            return string.Equals(merged.Endpoint, repository.Endpoint, StringComparison.Ordinal)
                ? null
                : merged.Endpoint;
        }
        catch (Exception exception) when (exception is YamlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
