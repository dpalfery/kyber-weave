using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Networking;

namespace KyberWeave.Core.Arbiter.Rules;

/// <summary>
/// Validates Arbiter rules and configuration against the permanent
/// <c>KW-ARB-CONFIG-001</c> to <c>-010</c> diagnostics. Every id is permanent.
/// </summary>
/// <remarks>
/// Hints use <see cref="Docs.Validation.DocSpecValidator"/> nearest-match rule over
/// <see cref="Text.StringDistance.Levenshtein"/> distance, so a typo'd fact, lens or
/// answer points at what was meant. The loader stays lenient and throws only on
/// malformed structure; everything this validator can name becomes a diagnostic so
/// that <c>validate</c> reports all findings at once.
/// </remarks>
public static class RuleValidator
{
    /// <summary>The <c>arbiter:</c> section or user override is malformed. The file is named.</summary>
    public const string MalformedSection = "KW-ARB-CONFIG-001";

    /// <summary>A rule reads a fact its trigger does not supply. Hints at the nearest fact.</summary>
    public const string UnknownFact = "KW-ARB-CONFIG-002";

    /// <summary>An unknown lens in <c>instructions-from</c>. Hints at the nearest lens.</summary>
    public const string UnknownLens = "KW-ARB-CONFIG-003";

    /// <summary>An answer with no effect.</summary>
    public const string AnswerWithoutEffect = "KW-ARB-CONFIG-004";

    /// <summary>A duplicate, retired or reserved-prefix id, or a non-overridable field on a shipped rule.</summary>
    public const string DuplicateOrReservedId = "KW-ARB-CONFIG-005";

    /// <summary>Model rules are switched off because the provider is <c>none</c>.</summary>
    public const string ModelRulesSwitchedOff = "KW-ARB-CONFIG-006";

    /// <summary>The configured model is not in an enabled step-1 rule's <c>tuned-for</c>, or has no known budget.</summary>
    public const string UntunedModel = "KW-ARB-CONFIG-007";

    /// <summary>An effect not allowed for the rule's trigger family, or a remapped <c>undecidable</c>.</summary>
    public const string ForeignEffect = "KW-ARB-CONFIG-008";

    /// <summary>The user override holds a key other than <c>provider</c>.</summary>
    public const string UserOverrideKey = "KW-ARB-CONFIG-009";

    /// <summary>A non-loopback endpoint uses <c>http</c>. The key is never sent over plain HTTP.</summary>
    public const string PlainHttpEndpoint = "KW-ARB-CONFIG-010";

    /// <summary>
    /// Validates the effective configuration: facts (<c>-002</c>), lenses (<c>-003</c>),
    /// answer coverage (<c>-004</c>), the provider-switched-off notice (<c>-006</c>),
    /// family effects (<c>-008</c>) and the endpoint scheme (<c>-010</c>).
    /// </summary>
    public static IReadOnlyList<Diagnostic> Validate(
        ArbiterConfig config,
        string? filePath = null,
        IReadOnlyCollection<string>? lensNames = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        List<Diagnostic> diagnostics = [];
        IReadOnlySet<string> lenses = new HashSet<string>(
            lensNames ?? ArbiterSquadCatalog.LoadEmbedded().Lenses.Keys, StringComparer.Ordinal);

        foreach (ArbiterRule rule in config.Rules)
            ValidateRule(rule, config.Rules, lenses, filePath, diagnostics);

        if (config.Provider.Kind == ArbiterProviderKind.None &&
            config.Rules.Any(rule => rule.Enabled && rule.Ask is not null))
        {
            diagnostics.Add(new Diagnostic(
                ModelRulesSwitchedOff,
                Severity.Warning,
                "The provider is 'none', so enabled step-1 rules answer undecidable.",
                "arbiter.provider",
                filePath,
                "Set arbiter.provider.kind to systemone, or disable the step-1 rules."));
        }

        Diagnostic? endpoint = CheckEndpoint(config.Provider.Endpoint, filePath);
        if (endpoint is not null)
            diagnostics.Add(endpoint);

        return diagnostics;
    }

    /// <summary>
    /// Validates raw host rule entries against the shipped id set: malformed entries
    /// (<c>-001</c>) and duplicate, reserved-prefix or non-overridable-field ids
    /// (<c>-005</c>).
    /// </summary>
    internal static IReadOnlyList<Diagnostic> ValidateHostEntries(
        IReadOnlyList<ArbiterRuleYaml> entries,
        IReadOnlySet<string> shippedIds,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(shippedIds);
        List<Diagnostic> diagnostics = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (ArbiterRuleYaml entry in entries)
        {
            string id = entry.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(id))
            {
                diagnostics.Add(new Diagnostic(
                    MalformedSection,
                    Severity.Error,
                    "arbiter.rules has an entry with no id. Every rule needs one.",
                    "arbiter.rules",
                    filePath));
                continue;
            }

            if (!seen.Add(id))
            {
                diagnostics.Add(new Diagnostic(
                    DuplicateOrReservedId,
                    Severity.Error,
                    $"arbiter.rules declares '{id}' more than once.",
                    id,
                    filePath,
                    "Give each rule its own id."));
                continue;
            }

            if (shippedIds.Contains(id))
            {
                string? violation = ArbiterConfigLoader.FindOverrideViolation(entry);
                if (violation is not null)
                {
                    diagnostics.Add(new Diagnostic(
                        DuplicateOrReservedId,
                        Severity.Error,
                        violation,
                        id,
                        filePath,
                        "Add a host rule for anything beyond the overridable fields."));
                }

                continue;
            }

            if (id.StartsWith(ArbiterConfigLoader.ShippedIdPrefix, StringComparison.Ordinal))
            {
                diagnostics.Add(new Diagnostic(
                    DuplicateOrReservedId,
                    Severity.Error,
                    $"rule '{id}' uses the reserved '{ArbiterConfigLoader.ShippedIdPrefix}' prefix.",
                    id,
                    filePath,
                    "Host rules use their own ids, without that prefix."));
                continue;
            }

            if (!ArbiterConfigLoader.TryConvertRules(entry, out _, out string? error))
            {
                diagnostics.Add(new Diagnostic(
                    MalformedSection,
                    Severity.Error,
                    error ?? $"rule '{id}' is malformed.",
                    id,
                    filePath));
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// The <c>KW-ARB-CONFIG-007</c> check <c>doctor</c> calls: warns for every enabled
    /// step-1 rule whose <c>tuned-for</c> does not name the configured model, and when
    /// the model has no known state budget.
    /// </summary>
    public static IReadOnlyList<Diagnostic> CheckModelTuning(ArbiterConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        List<Diagnostic> diagnostics = [];
        string model = config.Provider.Model;

        foreach (ArbiterRule rule in config.Rules)
        {
            if (!rule.Enabled || rule.Ask is null)
                continue;

            if (rule.Ask.TunedFor is null || !rule.Ask.TunedFor.Contains(model, StringComparer.Ordinal))
            {
                diagnostics.Add(new Diagnostic(
                    UntunedModel,
                    Severity.Warning,
                    $"rule '{rule.Id}' threshold is not tuned for model '{model}'.",
                    rule.Id,
                    null,
                    "Tune the threshold for this model, or pin the model the thresholds were set for."));
            }
        }

        if (!TryGetStateBudget(model, out _) &&
            config.Rules.Any(rule => rule.Enabled && rule.Ask is not null))
        {
            diagnostics.Add(new Diagnostic(
                UntunedModel,
                Severity.Warning,
                $"model '{model}' has no known state budget; the smallest known budget applies.",
                "arbiter.provider",
                null,
                "Use a known model (jev-*, nimble, tev1) or confirm the budget before judging evidence."));
        }

        return diagnostics;
    }

    /// <summary>
    /// Validates the user override: it may hold <c>provider:</c> and nothing else
    /// (<c>KW-ARB-CONFIG-009</c>).
    /// </summary>
    internal static IReadOnlyList<Diagnostic> ValidateUserOverride(
        ArbiterYamlSection? section,
        string? filePath = null)
    {
        List<Diagnostic> diagnostics = [];
        if (section is null)
            return diagnostics;

        if (section.Enabled is not null)
        {
            diagnostics.Add(new Diagnostic(
                UserOverrideKey,
                Severity.Error,
                "The user override holds 'enabled'; it may hold provider and nothing else.",
                "arbiter.enabled",
                filePath,
                "Move enabled to the repository configuration; the override holds only provider."));
        }

        if (section.Rules is not null)
        {
            diagnostics.Add(new Diagnostic(
                UserOverrideKey,
                Severity.Error,
                "The user override holds 'rules'; it may hold provider and nothing else.",
                "arbiter.rules",
                filePath,
                "Move rules to the repository configuration; the override holds only provider."));
        }

        return diagnostics;
    }

    /// <summary>Whether <paramref name="model"/> has a known step-1 state budget, and which.</summary>
    /// <remarks>
    /// Budgets from the provider table: <c>jev-*</c> 32,000, <c>nimble</c> 8,000,
    /// <c>tev1</c> 2,000 tokens. Unknown models get the smallest known budget.
    /// </remarks>
    internal static bool TryGetStateBudget(string model, out int budget)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.StartsWith("jev-", StringComparison.Ordinal))
        {
            budget = 32_000;
            return true;
        }

        if (string.Equals(model, "nimble", StringComparison.Ordinal))
        {
            budget = 8_000;
            return true;
        }

        if (model.StartsWith("tev1", StringComparison.Ordinal))
        {
            budget = 2_000;
            return true;
        }

        budget = 2_000;
        return false;
    }

    private static void ValidateRule(
        ArbiterRule rule,
        IReadOnlyList<ArbiterRule> allRules,
        IReadOnlySet<string> lenses,
        string? filePath,
        List<Diagnostic> diagnostics)
    {
        ArbiterTriggerFamily family;
        try
        {
            family = ArbiterTriggerFamilies.ForTrigger(rule.Trigger);
        }
        catch (ArgumentException)
        {
            diagnostics.Add(new Diagnostic(
                MalformedSection,
                Severity.Error,
                $"rule '{rule.Id}' trigger '{rule.Trigger}' is not known.",
                rule.Id,
                filePath,
                $"Use one of: {string.Join(", ", ArbiterTriggerFamilies.KnownTriggers)}."));
            return;
        }

        HashSet<string> knownIds = new(allRules.Select(r => r.Id), StringComparer.Ordinal);
        if (!ArbiterTriggerCatalog.TryGetFacts(rule.Trigger, out IReadOnlySet<string> supplied))
            supplied = new HashSet<string>(StringComparer.Ordinal);

        foreach (string fact in FactsRead(rule))
        {
            if (ArbiterTriggerCatalog.IsAnswerRef(fact))
            {
                string referenced = fact["rules.".Length..^".answer".Length];
                if (!knownIds.Contains(referenced))
                {
                    diagnostics.Add(new Diagnostic(
                        UnknownFact,
                        Severity.Error,
                        $"rule '{rule.Id}' reads step-0 answer '{fact}' from an unknown rule.",
                        rule.Id,
                        filePath,
                        NearestHint(referenced, knownIds, "rule")));
                }

                continue;
            }

            if (!supplied.Contains(fact))
            {
                diagnostics.Add(new Diagnostic(
                    UnknownFact,
                    Severity.Error,
                    $"rule '{rule.Id}' reads fact '{fact}' its trigger '{rule.Trigger}' does not supply.",
                    rule.Id,
                    filePath,
                    NearestHint(fact, supplied, "fact")));
            }
        }

        if (rule.Ask?.InstructionsFrom is not null)
            ValidateInstructionsFrom(rule, lenses, filePath, diagnostics);

        string[] missing = rule.Answers.Where(answer => !rule.Effects.ContainsKey(answer)).ToArray();
        if (missing.Length > 0)
        {
            diagnostics.Add(new Diagnostic(
                AnswerWithoutEffect,
                Severity.Error,
                $"rule '{rule.Id}' answers {string.Join(", ", missing.Select(a => $"'{a}'"))} with no effect.",
                rule.Id,
                filePath,
                "Map every answer to an effect."));
        }

        IReadOnlySet<string> allowed = ArbiterTriggerFamilies.AllowedEffects(family);
        string[] foreign = rule.Effects
            .Where(pair => !allowed.Contains(pair.Value))
            .Select(pair => $"'{pair.Key}' -> '{pair.Value}'")
            .ToArray();
        if (foreign.Length > 0)
        {
            diagnostics.Add(new Diagnostic(
                ForeignEffect,
                Severity.Error,
                $"rule '{rule.Id}' uses effects its trigger family does not allow: {string.Join(", ", foreign)}.",
                rule.Id,
                filePath,
                $"Allowed effects for '{rule.Trigger}': {string.Join(", ", allowed)}."));
        }

        if (rule.Answers.Contains(RuleEngine.Undecidable, StringComparer.Ordinal))
        {
            diagnostics.Add(new Diagnostic(
                ForeignEffect,
                Severity.Error,
                $"rule '{rule.Id}' answers '{RuleEngine.Undecidable}'; a host cannot remap undecidable.",
                rule.Id,
                filePath,
                "Remove that answer; undecidable maps to the family's fixed effect."));
        }
    }

    private static void ValidateInstructionsFrom(
        ArbiterRule rule,
        IReadOnlySet<string> lenses,
        string? filePath,
        List<Diagnostic> diagnostics)
    {
        string source = rule.Ask!.InstructionsFrom!;
        if (!source.StartsWith("lens:", StringComparison.Ordinal))
            return;

        string name = source["lens:".Length..];
        if (string.IsNullOrEmpty(name))
        {
            // Dynamic dispatch on the lens.name fact: only meaningful when the rule reads it.
            if (!FactsRead(rule).Contains("lens.name", StringComparer.Ordinal))
            {
                diagnostics.Add(new Diagnostic(
                    UnknownLens,
                    Severity.Error,
                    $"rule '{rule.Id}' resolves instructions from the lens dynamically but never reads 'lens.name'.",
                    rule.Id,
                    filePath,
                    "Read the lens.name fact, or name one lens explicitly."));
            }

            return;
        }

        if (!lenses.Contains(name))
        {
            diagnostics.Add(new Diagnostic(
                UnknownLens,
                Severity.Error,
                $"rule '{rule.Id}' resolves instructions from unknown lens '{name}'.",
                rule.Id,
                filePath,
                NearestHint(name, lenses, "lens")));
        }
    }

    private static IEnumerable<string> FactsRead(ArbiterRule rule)
    {
        HashSet<string> facts = new(StringComparer.Ordinal);
        if (rule.Decide is not null)
        {
            foreach (RuleDecideClause clause in rule.Decide)
                CollectPredicateFacts(clause.When, facts);
        }

        if (rule.Ask is not null)
        {
            foreach (string value in rule.Ask.State.Values)
                facts.Add(value);

            if (rule.Ask.When is not null)
            {
                // Gated on a step-0 answer, written "<rule-id>=<answer>".
                string reference = rule.Ask.When.Trim();
                int separator = reference.IndexOf('=', StringComparison.Ordinal);
                string id = separator < 0 ? reference : reference[..separator];
                facts.Add($"rules.{id}.answer");
            }
        }

        return facts;
    }

    private static void CollectPredicateFacts(RulePredicate predicate, HashSet<string> facts)
    {
        foreach (RulePredicate child in predicate.Children)
            CollectPredicateFacts(child, facts);

        if (predicate.Fact is not null)
            facts.Add(predicate.Fact);

        if (predicate.Operand is { IsFactRef: true, FactName: not null })
            facts.Add(predicate.Operand.FactName);
    }

    private static string? NearestHint(string value, IEnumerable<string> candidates, string kind)
    {
        string? nearest = DocSpecValidator.Nearest(value, candidates);
        return nearest is null
            ? null
            : $"Did you mean '{nearest}'? No other known {kind} is closer.";
    }

    private static Diagnostic? CheckEndpoint(string endpoint, string? filePath)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new Diagnostic(
                MalformedSection,
                Severity.Error,
                $"arbiter.provider.endpoint '{endpoint}' is not an absolute http(s) URL.",
                "arbiter.provider",
                filePath,
                "Use https://api.typesafe.ai/v1, or an http(s) Ollama URL.");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !LoopbackAddress.IsLoopbackHost(uri.Host))
        {
            return new Diagnostic(
                PlainHttpEndpoint,
                Severity.Error,
                $"arbiter.provider.endpoint '{endpoint}' uses plain HTTP on a non-loopback host.",
                "arbiter.provider",
                filePath,
                "Use https; the key is never sent over plain HTTP.");
        }

        return null;
    }
}
