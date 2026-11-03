using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>The <c>systemone</c> wire format: one batched request per trigger.</summary>
/// <remarks>
/// Request: <c>{"state": ..., "model": ..., "questions": {"&lt;rule-id&gt;": ...}}</c>,
/// where each question carries <c>type</c> and a non-empty <c>instructions</c>,
/// plus <c>criteria</c> (an object for <c>choice</c>, an array of levels for
/// <c>score</c>). The shared state nests one object per rule id, so every
/// question traces to its rule's declared <c>state</c> fields and no fact
/// outside those declarations is sent. How the batched state object is laid
/// out is the implementer's choice; this nesting is that choice.
/// Response answers are parsed by <c>type</c>: <c>confidence</c> is optional
/// (<c>noul</c> never carries it), <c>usage</c> is optional, and
/// <c>probabilities</c> is an unordered dictionary that the client does not
/// depend on the order of.
/// </remarks>
internal static class SystemOneContracts
{
    /// <summary>The path appended to the provider endpoint.</summary>
    public const string SystemOnePath = "/systemone";

    /// <summary>Appends <see cref="SystemOnePath"/> to the provider endpoint.</summary>
    internal static Uri RequestUri(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return new Uri(endpoint.AbsoluteUri.TrimEnd('/') + SystemOnePath);
    }

    /// <summary>
    /// Whether the rule's question is gated on step-0 answers. A rule without
    /// <c>when</c> always asks; a gated rule asks only when
    /// <c>rules.&lt;id&gt;.answer</c> matches. With no step-0 answers supplied
    /// the caller is assumed to have pre-selected, so gated rules ask.
    /// </summary>
    internal static bool WhenMatches(RuleAsk ask, IReadOnlyDictionary<string, string>? step0Answers)
    {
        ArgumentNullException.ThrowIfNull(ask);
        if (ask.When is null || step0Answers is null)
        {
            return true;
        }

        int separator = ask.When.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        string ruleId = ask.When[..separator];
        string expected = ask.When[(separator + 1)..];
        return step0Answers.TryGetValue(ruleId, out string? actual)
            && string.Equals(actual, expected, StringComparison.Ordinal);
    }

    /// <summary>Builds the batched request body for the selected step-1 rules.</summary>
    internal static SystemOneBuiltRequest BuildRequest(
        IReadOnlyList<ArbiterRule> askRules,
        ArbiterFactSet facts,
        string model,
        Func<ArbiterRule, string?>? instructionsResolver)
    {
        ArgumentNullException.ThrowIfNull(askRules);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(model);

        Dictionary<string, object?> state = new(StringComparer.Ordinal);
        Dictionary<string, object?> questions = new(StringComparer.Ordinal);
        List<string> ruleIds = new(askRules.Count);
        int longestQuestionTokens = 0;
        foreach (ArbiterRule rule in askRules)
        {
            RuleAsk ask = rule.Ask!;
            Dictionary<string, object?> fields = new(StringComparer.Ordinal);
            foreach ((string field, string factName) in ask.State)
            {
                fields[field] = facts.TryGet(factName, out ArbiterFact? fact) ? fact.Value : null;
            }

            state[rule.Id] = fields;
            Dictionary<string, object?> question = new(StringComparer.Ordinal)
            {
                ["type"] = ask.Type,
                ["instructions"] = ResolveInstructions(rule, ask, instructionsResolver),
            };
            if (string.Equals(ask.Type, "choice", StringComparison.Ordinal) && ask.Criteria is not null)
            {
                question["criteria"] = ask.Criteria;
            }
            else if (string.Equals(ask.Type, "score", StringComparison.Ordinal) && ask.Levels is not null)
            {
                question["criteria"] = ask.Levels;
            }

            questions[rule.Id] = question;
            ruleIds.Add(rule.Id);
            longestQuestionTokens = Math.Max(
                longestQuestionTokens,
                StateBudget.EstimateTokens(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(question))));
        }

        string stateJson = JsonSerializer.Serialize(state);
        Dictionary<string, object?> body = new(StringComparer.Ordinal)
        {
            ["state"] = state,
            ["model"] = model,
            ["questions"] = questions,
        };
        string bodyJson = JsonSerializer.Serialize(body);
        return new SystemOneBuiltRequest(
            bodyJson,
            ruleIds,
            StateBudget.EstimateTokens(Encoding.UTF8.GetByteCount(stateJson)),
            longestQuestionTokens,
            StateBudget.EstimateTokens(bodyJson),
            Encoding.UTF8.GetByteCount(bodyJson));
    }

    /// <summary>
    /// Resolves a question's instructions: the injected resolver first (lens
    /// text or repository file), else the rule's own question. The result is
    /// always a non-empty string, as the wire format requires.
    /// </summary>
    internal static string ResolveInstructions(
        ArbiterRule rule,
        RuleAsk ask,
        Func<ArbiterRule, string?>? instructionsResolver)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(ask);
        string? resolved = instructionsResolver?.Invoke(rule);
        string candidate = string.IsNullOrWhiteSpace(resolved) ? rule.Question : resolved;
        if (string.Equals(ask.Type, "noul", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(ask.Question))
        {
            candidate += "\n\n" + ask.Question;
        }

        return string.IsNullOrWhiteSpace(candidate) ? $"Answer the {rule.Id} question." : candidate;
    }

    /// <summary>
    /// Parses the response envelope. Answers stay as cloned elements for
    /// per-rule parsing; a missing <c>usage</c> (or an unreadable one) yields
    /// no usage rather than failing the whole body.
    /// </summary>
    internal static bool TryParseResponse(string body, out SystemOneParsedResponse? response)
    {
        response = null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("answers", out JsonElement answers)
                || answers.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? model = root.TryGetProperty("model", out JsonElement modelValue)
                && modelValue.ValueKind == JsonValueKind.String
                ? modelValue.GetString()
                : null;
            Dictionary<string, JsonElement> byRule = new(StringComparer.Ordinal);
            foreach (JsonProperty answer in answers.EnumerateObject())
            {
                byRule[answer.Name] = answer.Value.Clone();
            }

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usage)
                && usage.ValueKind == JsonValueKind.Object
                && TryReadNonNegativeInt(usage, "input_tokens", out int input)
                && TryReadNonNegativeInt(usage, "output_tokens", out int output))
            {
                inputTokens = input;
                outputTokens = output;
            }

            response = new SystemOneParsedResponse(model, byRule, inputTokens, outputTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads a <c>choice</c> answer: the option name and optional confidence.</summary>
    internal static bool TryReadChoice(JsonElement answer, out string? choice, out double? confidence)
    {
        choice = null;
        confidence = null;
        if (!IsType(answer, "choice")
            || !answer.TryGetProperty("choice", out JsonElement choiceValue)
            || choiceValue.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(choiceValue.GetString()))
        {
            return false;
        }

        choice = choiceValue.GetString();
        return TryReadOptionalConfidence(answer, out confidence);
    }

    /// <summary>Reads a <c>score</c> answer: the numeric score and optional confidence.</summary>
    internal static bool TryReadScore(JsonElement answer, out double score, out double? confidence)
    {
        score = 0;
        confidence = null;
        if (!IsType(answer, "score")
            || !answer.TryGetProperty("score", out JsonElement scoreValue)
            || scoreValue.ValueKind != JsonValueKind.Number
            || !scoreValue.TryGetDouble(out score)
            || !double.IsFinite(score))
        {
            return false;
        }

        return TryReadOptionalConfidence(answer, out confidence);
    }

    /// <summary>Reads a <c>noul</c> answer: the probability. Any confidence is ignored.</summary>
    internal static bool TryReadNoul(JsonElement answer, out double probability)
    {
        probability = 0;
        return IsType(answer, "noul")
            && answer.TryGetProperty("noul", out JsonElement noulValue)
            && noulValue.ValueKind == JsonValueKind.Number
            && noulValue.TryGetDouble(out probability)
            && double.IsFinite(probability)
            && probability >= 0
            && probability <= 1;
    }

    private static bool IsType(JsonElement answer, string type) =>
        answer.ValueKind == JsonValueKind.Object
        && answer.TryGetProperty("type", out JsonElement typeValue)
        && typeValue.ValueKind == JsonValueKind.String
        && string.Equals(typeValue.GetString(), type, StringComparison.Ordinal);

    private static bool TryReadOptionalConfidence(JsonElement answer, out double? confidence)
    {
        confidence = null;
        if (!answer.TryGetProperty("confidence", out JsonElement confidenceValue))
        {
            return true;
        }

        if (confidenceValue.ValueKind != JsonValueKind.Number
            || !confidenceValue.TryGetDouble(out double value)
            || !double.IsFinite(value))
        {
            return false;
        }

        confidence = value;
        return true;
    }

    private static bool TryReadNonNegativeInt(JsonElement parent, string propertyName, out int value)
    {
        value = 0;
        return parent.TryGetProperty(propertyName, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value)
            && value >= 0;
    }
}

/// <summary>The serialized batched request with its budget inputs.</summary>
/// <param name="BodyJson">The request body.</param>
/// <param name="RuleIds">The asked rule ids, in order.</param>
/// <param name="StateTokens">Estimated tokens of the shared state object.</param>
/// <param name="LongestQuestionTokens">Estimated tokens of the largest single question.</param>
/// <param name="TotalRequestTokens">Estimated tokens of the whole body.</param>
/// <param name="BodyBytes">The UTF-8 body size in bytes.</param>
internal sealed record SystemOneBuiltRequest(
    string BodyJson,
    IReadOnlyList<string> RuleIds,
    int StateTokens,
    int LongestQuestionTokens,
    int TotalRequestTokens,
    int BodyBytes);

/// <summary>The parsed response envelope.</summary>
/// <param name="Model">The model that answered, when the response names one.</param>
/// <param name="AnswersByRule">Raw answers keyed by rule id.</param>
/// <param name="InputTokens">Reported input tokens, when usage was readable.</param>
/// <param name="OutputTokens">Reported output tokens, when usage was readable.</param>
internal sealed record SystemOneParsedResponse(
    string? Model,
    IReadOnlyDictionary<string, JsonElement> AnswersByRule,
    int? InputTokens,
    int? OutputTokens);
