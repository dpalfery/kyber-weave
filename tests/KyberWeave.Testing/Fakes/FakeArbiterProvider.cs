using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one fake for <see cref="IArbiterProvider"/>: it records every request and answers from what the test
/// configured — per rule, per call, or by throwing.
/// </summary>
/// <remarks>
/// Simple cases use one of the factories (<see cref="None"/>, <see cref="Answering"/>) or the answer
/// properties. A case that no property covers scripts the whole result with <see cref="Script"/> or
/// <see cref="Respond"/>, so it stays a configuration of this fake rather than another local double.
/// </remarks>
internal sealed class FakeArbiterProvider : IArbiterProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeArbiterProviderCall> _calls = [];

    /// <summary>A provider with nothing behind it: no request leaves, and every asked rule is not evaluated.</summary>
    internal static FakeArbiterProvider None() => new()
    {
        DefaultAnswer = ArbiterStep1BatchResult.NotEvaluated,
        Model = null,
        RequestSent = false,
    };

    /// <summary>A provider that answers every asked rule with <paramref name="answer"/> at <paramref name="confidence"/>.</summary>
    internal static FakeArbiterProvider Answering(string answer, double confidence = 0.95, double? threshold = null) =>
        new() { AnswerAsked = answer, Confidence = confidence, Threshold = threshold };

    /// <summary>A provider that answers one named rule and leaves the rest unanswered.</summary>
    /// <param name="ruleId">The rule it answers.</param>
    /// <param name="answer">The answer it gives.</param>
    /// <param name="confidence">The confidence it reports.</param>
    /// <param name="requestSent">Whether the result reports that a request left the machine.</param>
    /// <param name="fault">The exception to raise instead of answering, when the test models a provider failure.</param>
    internal static FakeArbiterProvider AnsweringRule(
        string ruleId,
        string answer,
        double confidence = 0.95,
        bool requestSent = true,
        Exception? fault = null) =>
        new()
        {
            Answers = { [ruleId] = answer },
            Confidence = confidence,
            RequestSent = requestSent,
            Fault = fault,
        };

    /// <summary>A provider that answers the named rules and leaves the rest unanswered.</summary>
    internal static FakeArbiterProvider AnsweringByRule(params (string RuleId, string Answer)[] answers)
    {
        FakeArbiterProvider provider = new();
        foreach ((string ruleId, string answer) in answers)
        {
            provider.Answers[ruleId] = answer;
        }

        return provider;
    }

    /// <summary>A provider that raises <paramref name="fault"/> for every request.</summary>
    internal static FakeArbiterProvider Throwing(Exception fault) => new() { Fault = fault };

    /// <summary>Every request received, in call order.</summary>
    internal IReadOnlyList<FakeArbiterProviderCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>How many requests the provider received.</summary>
    internal int CallCount => Calls.Count;

    /// <summary>Every rule asked, in request order, keeping repeats of one rule id.</summary>
    internal IReadOnlyList<ArbiterRule> AskedRules => Calls.SelectMany(call => call.Rules).ToArray();

    /// <summary>Every rule id asked, in request order, keeping repeats of one rule id.</summary>
    internal IReadOnlyList<string> AskedRuleIds => AskedRules.Select(rule => rule.Id).ToArray();

    /// <summary>The facts set sent with each request, in request order.</summary>
    internal IReadOnlyList<ArbiterFactSet> FactsSent => Calls.Select(call => call.Facts).ToArray();

    /// <summary>How many rules each request carried, in request order.</summary>
    internal IReadOnlyList<int> BatchSizes => Calls.Select(call => call.Rules.Count).ToArray();

    /// <summary>The most recent request, or null when the provider was never called.</summary>
    internal FakeArbiterProviderCall? LastCall => Calls is { Count: > 0 } calls ? calls[^1] : null;

    /// <summary>The answer for every rule in a batch, asked or not.</summary>
    internal string? AnswerAll { get; init; }

    /// <summary>The answer for every rule that carries an ask.</summary>
    internal string? AnswerAsked { get; init; }

    /// <summary>The answer for a named rule; wins over <see cref="AnswerAll"/> and <see cref="AnswerAsked"/>.</summary>
    internal Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

    /// <summary>Answers a rule the maps and defaults do not name; null leaves the rule unanswered.</summary>
    internal Func<FakeArbiterProviderCall, ArbiterRule, string?>? AnswerRule { get; init; }

    /// <summary>The answer for a rule no other source answered; null leaves the rule out of the result.</summary>
    internal string? DefaultAnswer { get; init; }

    /// <summary>
    /// Whether a rule answered by <see cref="DefaultAnswer"/> also carries a score. Off by default: a
    /// <c>not-evaluated</c> default answer reports no confidence, as a provider that never answered does.
    /// </summary>
    internal bool ScoreDefaultAnswer { get; init; }

    /// <summary>The confidence recorded for every answered rule without an explicit score; null records no score.</summary>
    internal double? Confidence { get; init; }

    /// <summary>The score recorded for every answered rule without an explicit score; wins over <see cref="Confidence"/>.</summary>
    internal ArbiterStep1Score? ScoreForAll { get; init; }

    /// <summary>The threshold recorded in a computed score; the rule's own floor or ceiling when null.</summary>
    internal double? Threshold { get; init; }

    /// <summary>An explicit score by rule id; wins over <see cref="Confidence"/>.</summary>
    internal Dictionary<string, ArbiterStep1Score> Scores { get; } = new(StringComparer.Ordinal);

    /// <summary>The model named in every result; null reports none.</summary>
    internal string? Model { get; init; } = "fake-arbiter-provider";

    /// <summary>The usage reported in every result, when a test needs one.</summary>
    internal ArbiterStep1Usage? Usage { get; init; }

    /// <summary>Whether every result reports that a request left the machine.</summary>
    internal bool RequestSent { get; init; } = true;

    /// <summary>Raised for every request instead of answering.</summary>
    internal Exception? Fault { get; init; }

    /// <summary>Raised for the requests it returns an exception for, instead of answering; null answers.</summary>
    internal Func<FakeArbiterProviderCall, Exception?>? FaultWhen { get; init; }

    /// <summary>One whole result per call, in call order; the last entry repeats. Overrides the answer properties.</summary>
    internal IReadOnlyList<Func<FakeArbiterProviderCall, ArbiterStep1BatchResult>> Script { get; init; } = [];

    /// <summary>Builds the whole result for a call the script does not cover. Overrides the answer properties.</summary>
    internal Func<FakeArbiterProviderCall, ArbiterStep1BatchResult>? Respond { get; init; }

    /// <inheritdoc />
    public Task<ArbiterStep1BatchResult> AskStep1Async(
        IReadOnlyList<ArbiterRule> rules,
        ArbiterFactSet facts,
        IReadOnlyDictionary<string, string>? step0Answers = null,
        CancellationToken cancellationToken = default)
    {
        FakeArbiterProviderCall call;
        lock (_gate)
        {
            call = new FakeArbiterProviderCall(_calls.Count, rules, facts, step0Answers);
            _calls.Add(call);
        }

        if (FaultWhen?.Invoke(call) is { } whenFault)
        {
            throw whenFault;
        }

        if (Fault is not null)
        {
            throw Fault;
        }

        if (Script.Count > 0)
        {
            return Task.FromResult(Script[Math.Min(call.Index, Script.Count - 1)](call));
        }

        return Task.FromResult(Respond is null ? Answer(call) : Respond(call));
    }

    private ArbiterStep1BatchResult Answer(FakeArbiterProviderCall call)
    {
        Dictionary<string, string> answers = new(StringComparer.Ordinal);
        Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
        foreach (ArbiterRule rule in call.Rules)
        {
            string? answer =
                Answers.TryGetValue(rule.Id, out string? named) ? named
                : AnswerRule?.Invoke(call, rule)
                ?? (rule.Ask is not null ? AnswerAsked ?? AnswerAll : AnswerAll);
            bool defaulted = false;
            if (answer is null && rule.Ask is not null && DefaultAnswer is not null)
            {
                answer = DefaultAnswer;
                defaulted = true;
            }

            if (answer is null)
            {
                continue;
            }

            answers[rule.Id] = answer;
            if (defaulted && !ScoreDefaultAnswer)
            {
                continue;
            }

            if (Score(rule) is { } score)
            {
                scores[rule.Id] = score;
            }
        }

        return new ArbiterStep1BatchResult(answers, Model, Usage, RequestSent, scores);
    }

    private ArbiterStep1Score? Score(ArbiterRule rule)
    {
        if (Scores.TryGetValue(rule.Id, out ArbiterStep1Score? explicitScore))
        {
            return explicitScore;
        }

        if (ScoreForAll is not null)
        {
            return ScoreForAll;
        }

        return Confidence is { } confidence
            ? new ArbiterStep1Score(null, confidence, Threshold ?? rule.Ask?.ConfidenceAtLeast)
            : null;
    }
}

/// <summary>One request as <see cref="FakeArbiterProvider"/> recorded it.</summary>
/// <param name="Index">The zero-based call index, counting every request.</param>
/// <param name="Rules">The rules the request asked about, in order.</param>
/// <param name="Facts">The fact set the request carried.</param>
/// <param name="Step0Answers">The step-0 answers the request carried, when it carried any.</param>
internal sealed record FakeArbiterProviderCall(
    int Index,
    IReadOnlyList<ArbiterRule> Rules,
    ArbiterFactSet Facts,
    IReadOnlyDictionary<string, string>? Step0Answers)
{
    /// <summary>The rules that carry an ask, in order.</summary>
    internal IReadOnlyList<ArbiterRule> Questions => Rules.Where(rule => rule.Ask is not null).ToArray();

    /// <summary>Whether this request asked about <paramref name="ruleId"/>.</summary>
    internal bool Asks(string ruleId) => Rules.Any(rule => string.Equals(rule.Id, ruleId, StringComparison.Ordinal));

    /// <summary>The string value of a fact this request carried; empty when it is absent or not a string.</summary>
    internal string FactText(string name) =>
        Facts.TryGet(name, out ArbiterFact? fact) && fact.Value is string text ? text : string.Empty;

    /// <summary>The values of the facts a rule's ask reads, joined by newlines.</summary>
    internal string StateText(ArbiterRule rule) => string.Join(
        '\n',
        rule.Ask is null
            ? []
            : rule.Ask.State.Values.Select(name =>
                Facts.TryGet(name, out ArbiterFact? fact) ? fact.Value?.ToString() : null));
}
