using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Parsing;
using YamlDotNet.Core;

namespace KyberWeave.Core.Arbiter;

/// <summary>Loads, merges, and validates the <c>arbiter:</c> host configuration section.</summary>
public static class ArbiterConfigLoader
{
    private const string EmbeddedRulesResource = "Arbiter.default-rules.yml";

    /// <summary>The prefix every shipped rule id carries. Host rules must not use it.</summary>
    internal const string ShippedIdPrefix = "KW-ARB-";

    internal static ArbiterConfig Merge(ArbiterConfig defaults, ArbiterYamlSection? section)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (section is null)
            return defaults;

        // A rule id may expand to one rule per trigger, so shipped rules are keyed
        // by distinct id rather than by row.
        IReadOnlyDictionary<string, ArbiterRule> shippedById = defaults.Rules
            .GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        List<ArbiterRule> rules = new(defaults.Rules.Count + (section.Rules?.Count ?? 0));
        HashSet<string> overridden = new(StringComparer.Ordinal);
        HashSet<string> seenHost = new(StringComparer.Ordinal);
        List<ArbiterRule> hostRules = [];

        if (section.Rules is not null)
        {
            foreach (ArbiterRuleYaml entry in section.Rules)
            {
                string id = entry.Id?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(id))
                    throw new YamlException(
                        $"[{RuleValidator.MalformedSection}] arbiter.rules has an entry with no id. Every rule needs one.");

                if (!seenHost.Add(id))
                    throw new YamlException(
                        $"[{RuleValidator.DuplicateOrReservedId}] arbiter.rules declares '{id}' more than once.");

                if (shippedById.ContainsKey(id))
                {
                    string? violation = FindOverrideViolation(entry);
                    if (violation is not null)
                        throw new YamlException(violation);
                    overridden.Add(id);
                }
                else
                {
                    if (id.StartsWith(ShippedIdPrefix, StringComparison.Ordinal))
                        throw new YamlException(
                            $"[{RuleValidator.DuplicateOrReservedId}] rule '{id}' uses the reserved '{ShippedIdPrefix}' prefix. " +
                            "Host rules use their own ids, without that prefix.");
                    if (!TryConvertRules(entry, out IReadOnlyList<ArbiterRule>? hostConverted, out string? error))
                        throw new YamlException($"[{RuleValidator.MalformedSection}] {error}");
                    hostRules.AddRange(hostConverted);
                }
            }
        }

        foreach (ArbiterRule shipped in defaults.Rules)
        {
            rules.Add(overridden.Contains(shipped.Id)
                ? ApplyOverride(shipped, section.Rules!.First(entry => string.Equals(entry.Id?.Trim(), shipped.Id, StringComparison.Ordinal)))
                : shipped);
        }

        rules.AddRange(hostRules);

        return new ArbiterConfig
        {
            Enabled = section.Enabled ?? defaults.Enabled,
            Provider = MergeProvider(defaults.Provider, section.Provider),
            Rules = rules,
            OwnerFileKindMap = defaults.OwnerFileKindMap,
            LensPaths = defaults.LensPaths,
        };
    }

    /// <summary>Loads and merges the <c>arbiter:</c> section from a <c>kyber-weave.yml</c> file.</summary>
    public static ArbiterConfig LoadMerged(ArbiterConfig defaults, string yamlPath)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlPath);
        KyberWeaveYamlDocument document = KyberWeaveYamlParser.ParseFile(yamlPath);
        return Merge(defaults, document.Arbiter);
    }

    /// <summary>
    /// Applies the user override (<c>~/.config/kyber-weave/arbiter.yml</c>) to the
    /// repository provider field by field. It may hold <c>provider:</c> and nothing else.
    /// </summary>
    internal static ArbiterProviderConfig ApplyUserProviderOverride(
        ArbiterProviderConfig repository,
        ArbiterProviderYaml? userProvider)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (userProvider is null)
            return repository;

        ArbiterProviderKind kind = repository.Kind;
        if (userProvider.Kind is not null)
            kind = ParseProviderKind(userProvider.Kind);

        return new ArbiterProviderConfig(
            kind,
            userProvider.Endpoint ?? repository.Endpoint,
            userProvider.Model ?? repository.Model,
            userProvider.TimeoutMs ?? repository.TimeoutMs);
    }

    /// <summary>Loads the product defaults from the embedded <c>default-rules.yml</c>.</summary>
    internal static ArbiterConfig LoadEmbeddedDefaults()
    {
        Assembly assembly = typeof(ArbiterConfigLoader).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(EmbeddedRulesResource);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedRulesResource}' is missing. " +
                "The project must embed Arbiter/Rules/default-rules.yml.");
        }

        string yaml;
        using (StreamReader reader = new(stream, Encoding.UTF8))
            yaml = reader.ReadToEnd();

        ArbiterDefaultRulesYaml? document;
        try
        {
            document = MarkdownFrontmatterReader.Deserializer.Deserialize<ArbiterDefaultRulesYaml>(yaml);
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedRulesResource}' is not valid YAML: {exception.Message}",
                exception);
        }

        if (document?.Rules is null || document.Rules.Count == 0)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedRulesResource}' declares no rules.");
        }

        List<ArbiterRule> rules = new(document.Rules.Count);
        foreach (ArbiterRuleYaml entry in document.Rules)
        {
            if (!TryConvertRules(entry, out IReadOnlyList<ArbiterRule>? converted, out string? error))
            {
                throw new InvalidOperationException(
                    $"Embedded resource '{EmbeddedRulesResource}' is invalid: {error}");
            }

            rules.AddRange(converted);
        }

        return new ArbiterConfig
        {
            OwnerFileKindMap = ParseOwnerMap(document.OwnerFileKindMap),
            LensPaths = ParseLensPaths(document.LensPaths),
            Rules = rules,
        };
    }

    /// <summary>
    /// Converts one rule entry to <see cref="ArbiterRule"/> objects — one per trigger,
    /// since a rule may run on several triggers under one permanent id — returning
    /// false with an explanation when the entry is malformed. Total: the validator
    /// reports the explanation as <c>KW-ARB-CONFIG-001</c> instead of throwing.
    /// </summary>
    internal static bool TryConvertRules(
        ArbiterRuleYaml entry,
        [NotNullWhen(true)] out IReadOnlyList<ArbiterRule>? rules,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(entry);
        rules = null;

        string id = entry.Id?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            error = "arbiter.rules has an entry with no id. Every rule needs one.";
            return false;
        }

        if (!TryConvertTriggers(id, entry.Trigger, out IReadOnlyList<string>? triggers, out error))
            return false;
        if (string.IsNullOrWhiteSpace(entry.Question))
        {
            error = $"rule '{id}' has no question. A rule states the question it answers.";
            return false;
        }

        string question = entry.Question.Trim();
        if (entry.Answers is null || entry.Answers.Count == 0)
        {
            error = $"rule '{id}' declares no answers. List the answers the rule may produce.";
            return false;
        }

        List<string> answers = entry.Answers;
        if (answers.Any(string.IsNullOrWhiteSpace) || answers.Distinct(StringComparer.Ordinal).Count() != answers.Count)
        {
            error = $"rule '{id}' has blank or duplicate answers. Answers must be distinct non-empty names.";
            return false;
        }

        if (entry.Decide is not null)
        {
            List<RuleDecideClause> clauses = new(entry.Decide.Count);
            foreach (ArbiterDecideClauseYaml clause in entry.Decide)
            {
                if (string.IsNullOrWhiteSpace(clause.Answer))
                {
                    error = $"rule '{id}' has a decide entry with no answer.";
                    return false;
                }

                string answer = clause.Answer.Trim();
                if (!answers.Contains(answer, StringComparer.Ordinal))
                {
                    error = $"rule '{id}' answers '{clause.Answer}' in decide but does not declare it in answers.";
                    return false;
                }

                if (!TryConvertWhen(clause.When, out RulePredicate? predicate, out string? whenError))
                {
                    error = $"rule '{id}': {whenError}";
                    return false;
                }

                clauses.Add(new RuleDecideClause(predicate, answer));
            }

            if (clauses.Count == 0)
            {
                error = $"rule '{id}' declares an empty decide list. Omit decide or give it clauses.";
                rules = null;
                return false;
            }

            return FinishRules(id, triggers, question, answers, entry, clauses, out rules, out error);
        }

        return FinishRules(id, triggers, question, answers, entry, null, out rules, out error);
    }

    private static bool TryConvertTriggers(
        string id,
        object? trigger,
        [NotNullWhen(true)] out IReadOnlyList<string>? triggers,
        out string? error)
    {
        triggers = null;
        List<string> names = trigger switch
        {
            string single => string.IsNullOrWhiteSpace(single) ? [] : [single.Trim()],
            System.Collections.IEnumerable sequence and not string =>
                sequence.Cast<object?>()
                    .Select(item => item?.ToString()?.Trim() ?? string.Empty)
                    .ToList(),
            _ => [],
        };

        if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
        {
            error = $"rule '{id}' has no trigger. Use one of: {string.Join(", ", ArbiterTriggerFamilies.KnownTriggers)}.";
            return false;
        }

        foreach (string name in names)
        {
            if (!IsKnownTrigger(name, out string? triggerError))
            {
                error = triggerError;
                return false;
            }
        }

        triggers = names.Distinct(StringComparer.Ordinal).ToList();
        error = null;
        return true;
    }

    /// <summary>
    /// Reports why a shipped-rule override entry is invalid, or null when the entry
    /// only tunes overridable fields. Anything else must become a host rule.
    /// </summary>
    internal static string? FindOverrideViolation(ArbiterRuleYaml entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string id = entry.Id?.Trim() ?? string.Empty;

        if (entry.Trigger is not null ||
            entry.Question is not null ||
            entry.Answers is not null ||
            entry.Decide is not null ||
            entry.Ask is not null)
        {
            return $"[{RuleValidator.DuplicateOrReservedId}] rule '{id}' sets a field only host rules may set. " +
                "A shipped rule tunes only enabled, confidence-at-least / probability-below and effects; " +
                "add a host rule for anything else.";
        }

        if (entry.ConfidenceAtLeast is { } confidence && (confidence is <= 0 or > 1))
            return $"[{RuleValidator.MalformedSection}] rule '{id}' confidence-at-least must be within (0, 1].";

        if (entry.ProbabilityBelow is { } probability && (probability is < 0 or >= 1))
            return $"[{RuleValidator.MalformedSection}] rule '{id}' probability-below must be within [0, 1).";

        return null;
    }

    private static bool FinishRules(
        string id,
        IReadOnlyList<string> triggers,
        string question,
        List<string> answers,
        ArbiterRuleYaml entry,
        List<RuleDecideClause>? clauses,
        [NotNullWhen(true)] out IReadOnlyList<ArbiterRule>? rules,
        out string? error)
    {
        RuleAsk? ask = null;
        if (entry.Ask is not null)
        {
            if (!TryConvertAsk(id, entry.Ask, out ask, out error))
            {
                rules = null;
                return false;
            }
        }

        // Coverage is the validator's complaint (KW-ARB-CONFIG-004); the loader stays
        // lenient so that `validate` can report every finding at once instead of the
        // load failing on the first incomplete rule.
        Dictionary<string, string> effects = new(StringComparer.Ordinal);
        if (entry.Effects is not null)
        {
            foreach ((string answer, string effect) in entry.Effects)
            {
                if (!answers.Contains(answer, StringComparer.Ordinal))
                {
                    error = $"rule '{id}' maps an effect for undeclared answer '{answer}'.";
                    rules = null;
                    return false;
                }

                effects[answer] = effect;
            }
        }

        rules = triggers.Select(trigger => new ArbiterRule(
            id,
            trigger,
            question,
            [.. answers],
            new Dictionary<string, string>(effects, StringComparer.Ordinal),
            clauses,
            ask,
            entry.Enabled ?? true)).ToList();
        error = null;
        return true;
    }

    private static bool TryConvertAsk(
        string id,
        ArbiterAskYaml ask,
        [NotNullWhen(true)] out RuleAsk? result,
        out string? error)
    {
        result = null;

        if (ask.Type is not ("choice" or "score" or "noul"))
        {
            error = $"rule '{id}' ask type '{ask.Type}' is not supported. Use choice, score or noul.";
            return false;
        }

        if (ask.State is null || ask.State.Count == 0)
        {
            error = $"rule '{id}' ask declares no state. State maps named fields to facts, and to facts only.";
            return false;
        }

        if (ask.ConfidenceAtLeast is { } confidence && (confidence is <= 0 or > 1))
        {
            error = $"rule '{id}' confidence-at-least must be within (0, 1].";
            return false;
        }

        if (ask.ProbabilityBelow is { } probability && (probability is < 0 or >= 1))
        {
            error = $"rule '{id}' probability-below must be within [0, 1).";
            return false;
        }

        result = new RuleAsk(
            ask.Type!,
            new Dictionary<string, string>(ask.State, StringComparer.Ordinal),
            ask.Criteria is null ? null : new Dictionary<string, string>(ask.Criteria, StringComparer.Ordinal),
            ask.Levels is null ? null : [.. ask.Levels],
            ask.Question,
            ask.InstructionsFrom,
            ask.ConfidenceAtLeast,
            ask.ProbabilityBelow,
            ask.When,
            ask.TunedFor is null ? null : [.. ask.TunedFor]);
        error = null;
        return true;
    }

    private static bool TryConvertWhen(
        ArbiterWhenYaml? when,
        [NotNullWhen(true)] out RulePredicate? predicate,
        out string? error)
    {
        predicate = null;
        if (when is null)
        {
            error = "a decide entry has no when predicate.";
            return false;
        }

        bool hasAll = when.All is not null;
        bool hasAny = when.Any is not null;
        bool hasNot = when.Not is not null;
        int combinators = (hasAll ? 1 : 0) + (hasAny ? 1 : 0) + (hasNot ? 1 : 0);
        bool hasLeaf = when.Exists is not null || when.Equals is not null || when.In is not null ||
            when.Matches is not null || when.SubsetOf is not null || when.Intersects is not null || when.Count is not null;

        if (combinators > 1 || (combinators == 1 && hasLeaf))
        {
            error = "a when names either one combinator (all, any, not) or one fact and one operator, not both.";
            return false;
        }

        if (hasAll || hasAny)
        {
            List<ArbiterWhenYaml> children = (hasAll ? when.All : when.Any)!;
            if (children.Count == 0)
            {
                error = "an all/any combinator needs at least one child predicate.";
                return false;
            }

            List<RulePredicate> childPredicates = new(children.Count);
            foreach (ArbiterWhenYaml child in children)
            {
                if (!TryConvertWhen(child, out RulePredicate? childPredicate, out error))
                    return false;
                childPredicates.Add(childPredicate);
            }

            predicate = hasAll ? RulePredicate.All([.. childPredicates]) : RulePredicate.Any([.. childPredicates]);
            error = null;
            return true;
        }

        if (hasNot)
        {
            if (!TryConvertWhen(when.Not, out RulePredicate? child, out error))
                return false;
            predicate = RulePredicate.Not(child);
            error = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(when.Fact))
        {
            error = "a when names one fact and one operator; the fact is missing.";
            return false;
        }

        string fact = when.Fact.Trim();
        int operators = (when.Exists is not null ? 1 : 0) + (when.Equals is not null ? 1 : 0) +
            (when.In is not null ? 1 : 0) + (when.Matches is not null ? 1 : 0) +
            (when.SubsetOf is not null ? 1 : 0) + (when.Intersects is not null ? 1 : 0) +
            (when.Count is not null ? 1 : 0);
        if (operators != 1)
        {
            error = $"when on fact '{fact}' must name exactly one operator.";
            return false;
        }

        if (when.Exists is not null)
        {
            predicate = RulePredicate.Exists(fact, when.Exists.Value);
            error = null;
            return true;
        }

        if (when.Count is not null)
        {
            if (!TryConvertCountOperand(fact, when.Count, out RuleOperand? operand, out error))
                return false;
            predicate = RulePredicate.Count(fact, operand);
            error = null;
            return true;
        }

        object? raw = when.Equals ?? when.In ?? when.Matches ?? when.SubsetOf ?? when.Intersects;
        if (!TryConvertOperand(fact, raw, out RuleOperand? converted, out error))
            return false;

        predicate = when.Equals is not null ? RulePredicate.Equals(fact, converted)
            : when.In is not null ? RulePredicate.In(fact, converted)
            : when.Matches is not null ? RulePredicate.Matches(fact, converted)
            : when.SubsetOf is not null ? RulePredicate.SubsetOf(fact, converted)
            : RulePredicate.Intersects(fact, converted);
        error = null;
        return true;
    }

    private static bool TryConvertCountOperand(
        string fact,
        object count,
        [NotNullWhen(true)] out RuleOperand? operand,
        out string? error)
    {
        operand = null;
        switch (count)
        {
            case int expected:
                operand = RuleOperand.Literal(expected);
                error = null;
                return true;
            case long expectedLong when expectedLong >= int.MinValue && expectedLong <= int.MaxValue:
                operand = RuleOperand.Literal((int)expectedLong);
                error = null;
                return true;
            case string text when int.TryParse(text, out int parsed):
                operand = RuleOperand.Literal(parsed);
                error = null;
                return true;
            case string text when !string.IsNullOrWhiteSpace(text):
                // A fact reference resolving to an integer at evaluation time.
                operand = RuleOperand.Fact(text.Trim());
                error = null;
                return true;
            default:
                error = $"when on fact '{fact}' count must be an integer or a fact name.";
                return false;
        }
    }

    private static bool TryConvertOperand(
        string fact,
        object? raw,
        [NotNullWhen(true)] out RuleOperand? operand,
        out string? error)
    {
        operand = null;
        switch (raw)
        {
            case null:
                error = $"when on fact '{fact}' has no operand.";
                return false;
            case string text when IsFactOperand(text):
                operand = RuleOperand.Fact(text);
                error = null;
                return true;
            case string text:
                // YamlDotNet resolves untyped scalars to strings, so a predicate
                // written `equals: false` or `count: 0` arrives as text. Coerce the
                // unambiguous scalar shapes back; anything else stays a literal.
                operand = RuleOperand.Literal(CoerceScalar(text));
                error = null;
                return true;
            case bool or int or long or double:
                operand = RuleOperand.Literal(raw);
                error = null;
                return true;
            case System.Collections.IEnumerable sequence and not string:
                List<object?> items = [];
                foreach (object? item in sequence)
                {
                    if (item is string || item is bool || item is int || item is long || item is double)
                    {
                        items.Add(item);
                    }
                    else
                    {
                        error = $"when on fact '{fact}' lists only scalar operands.";
                        return false;
                    }
                }

                operand = RuleOperand.Literal(items);
                error = null;
                return true;
            default:
                error = $"when on fact '{fact}' has a mapping operand where a literal or fact name belongs.";
                return false;
        }
    }

    /// <summary>
    /// A string operand names another fact when it is one the triggers supply (or a
    /// step-0 answer reference); otherwise it is a literal. Dotted names that no
    /// trigger supplies stay literals so a glob such as <c>src/**</c> is never
    /// mistaken for a reference.
    /// </summary>
    private static bool IsFactOperand(string text) =>
        ArbiterTriggerCatalog.AllFacts.Contains(text) || ArbiterTriggerCatalog.IsAnswerRef(text);

    /// <summary>
    /// Coerces an untyped scalar back to its predicate shape. Only booleans and
    /// integers are unambiguous: a dotted numeric such as a version stays a string
    /// rather than risking a mistyped comparison.
    /// </summary>
    private static object CoerceScalar(string text)
    {
        if (bool.TryParse(text, out bool boolean))
            return boolean;
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int integer))
            return integer;
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long longer))
            return longer;
        return text;
    }

    private static bool IsKnownTrigger(string trigger, out string? error)
    {
        try
        {
            ArbiterTriggerFamilies.ForTrigger(trigger);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            error = $"rule trigger '{trigger}' is not known. {exception.Message}";
            return false;
        }
    }

    private static ArbiterRule ApplyOverride(ArbiterRule shipped, ArbiterRuleYaml entry)
    {
        RuleAsk? ask = shipped.Ask;
        if (ask is not null && (entry.ConfidenceAtLeast is not null || entry.ProbabilityBelow is not null))
        {
            ask = ask with
            {
                ConfidenceAtLeast = entry.ConfidenceAtLeast ?? ask.ConfidenceAtLeast,
                ProbabilityBelow = entry.ProbabilityBelow ?? ask.ProbabilityBelow,
            };
        }

        Dictionary<string, string> effects = new(shipped.Effects, StringComparer.Ordinal);
        if (entry.Effects is not null)
        {
            foreach ((string answer, string effect) in entry.Effects)
                effects[answer] = effect;
        }

        return shipped with
        {
            Enabled = entry.Enabled ?? shipped.Enabled,
            Ask = ask,
            Effects = effects,
        };
    }

    private static ArbiterProviderConfig MergeProvider(ArbiterProviderConfig defaults, ArbiterProviderYaml? section)
    {
        if (section is null)
            return defaults;

        ArbiterProviderKind kind = defaults.Kind;
        if (section.Kind is not null)
            kind = ParseProviderKind(section.Kind);

        int timeoutMs = section.TimeoutMs ?? defaults.TimeoutMs;
        if (timeoutMs <= 0)
            throw new YamlException("arbiter.provider.timeout-ms must be greater than zero.");

        return new ArbiterProviderConfig(
            kind,
            section.Endpoint ?? defaults.Endpoint,
            section.Model ?? defaults.Model,
            timeoutMs);
    }

    private static ArbiterProviderKind ParseProviderKind(string kind) =>
        kind.Trim().ToUpperInvariant() switch
        {
            "NONE" => ArbiterProviderKind.None,
            "SYSTEMONE" => ArbiterProviderKind.Systemone,
            _ => throw new YamlException(
                $"arbiter.provider.kind '{kind}' is not supported. Use none or systemone."),
        };

    private static IReadOnlyList<ArbiterFileKindMapEntry> ParseOwnerMap(List<ArbiterOwnerMapYaml>? entries)
    {
        if (entries is null)
            return [];
        return entries.Select(entry => new ArbiterFileKindMapEntry(
            entry.Patterns is null ? [] : [.. entry.Patterns],
            entry.Owner ?? string.Empty)).ToList();
    }

    private static IReadOnlyList<ArbiterLensPathEntry> ParseLensPaths(List<ArbiterLensPathYaml>? entries)
    {
        if (entries is null)
            return [];
        return entries.Select(entry => new ArbiterLensPathEntry(
            entry.Lens ?? string.Empty,
            entry.NotApplicableWhenAllMatch is null ? [] : [.. entry.NotApplicableWhenAllMatch])).ToList();
    }
}

/// <summary>The top-level shape of the embedded <c>default-rules.yml</c>.</summary>
internal sealed class ArbiterDefaultRulesYaml
{
    public List<ArbiterRuleYaml>? Rules { get; set; }

    public List<ArbiterOwnerMapYaml>? OwnerFileKindMap { get; set; }

    public List<ArbiterLensPathYaml>? LensPaths { get; set; }
}

/// <summary>One <c>owner-file-kind-map</c> row in the embedded rules.</summary>
internal sealed class ArbiterOwnerMapYaml
{
    public List<string>? Patterns { get; set; }

    public string? Owner { get; set; }
}

/// <summary>One <c>lens-paths</c> row in the embedded rules.</summary>
internal sealed class ArbiterLensPathYaml
{
    public string? Lens { get; set; }

    public List<string>? NotApplicableWhenAllMatch { get; set; }
}
