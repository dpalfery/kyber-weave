using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Tests.ProseRules;

/// <summary>What a graded case asks: whether a rule is stated, or whether its retired opposite is.</summary>
internal enum ProseCaseKind
{
    /// <summary>A home document; the rule must be judged stated.</summary>
    Stated,

    /// <summary>A negative control; the rule must be judged not stated.</summary>
    Control,

    /// <summary>A document that must not state the retired opposite.</summary>
    RetiredAbsent,

    /// <summary>A real document with the retired opposite inserted; it must be judged stated.</summary>
    RetiredControl,
}

/// <summary>How one graded case came out.</summary>
internal enum ProseOutcome
{
    /// <summary>The expected answer at or above the floor.</summary>
    Pass,

    /// <summary>The other answer, at or above the floor: a confident wrong answer.</summary>
    Wrong,

    /// <summary>The expected answer, but below the floor: it points the right way, so it is reported and does not fail.</summary>
    Warning,

    /// <summary>The other answer, below the floor: unsure and in the wrong direction.</summary>
    Unsure,

    /// <summary>The provider gave no readable answer: an error, a timeout, or a malformed body.</summary>
    Outage,

    /// <summary>The request was never sent because it is over the model's budget; a catalog or test bug.</summary>
    OverBudget,
}

/// <summary>One question for the grader: a rule, a document text and the answer it must get.</summary>
/// <param name="RuleId">The catalog rule.</param>
/// <param name="Kind">What is being asked.</param>
/// <param name="Path">The repository-relative document, or the document the control was built from.</param>
/// <param name="Statement">The rule, or the retired rule, the question asks about.</param>
/// <param name="Text">The document text sent.</param>
/// <param name="Note">How the text was built, when it is not the whole file.</param>
/// <param name="Contrast">For a retired rule, the rule that replaced it, so the grader can tell the two apart.</param>
internal sealed record ProseCase(string RuleId, ProseCaseKind Kind, string Path, string Statement, string Text, string? Note, string? Contrast = null)
{
    /// <summary>The answer this case must get.</summary>
    public string Expected => Kind is ProseCaseKind.Stated or ProseCaseKind.RetiredControl
        ? ProseGrader.StatedAnswer
        : ProseGrader.NotStatedAnswer;
}

/// <summary>One graded case's result.</summary>
internal sealed record ProseResult(ProseCase Case, ProseOutcome Outcome, string? Answer, double? Confidence, string? Detail);

/// <summary>
/// Asks Jev, through the product's <see cref="SystemOneClient"/>, whether each document states each rule.
/// One question per request: the client nests the state under each question's id, so batching
/// questions over one document would send the document once per question.
/// </summary>
internal sealed class ProseGrader : IDisposable
{
    /// <summary>The answer meaning the document states the rule.</summary>
    public const string StatedAnswer = "stated";

    /// <summary>The answer meaning it does not.</summary>
    public const string NotStatedAnswer = "not-stated";

    private const string QuestionId = "rule";
    private const string DocumentFact = "doc.text";

    private readonly SystemOneClient _client;
    private readonly RecordingHandler _recorder;
    private readonly string _model;
    private readonly double _floor;

    /// <summary>Creates a grader over <paramref name="transport"/>, which it owns.</summary>
    public ProseGrader(HttpMessageHandler transport, Uri endpoint, string model, string? key, double floor, int timeoutMs)
    {
        _recorder = new RecordingHandler(transport);
        _model = model;
        _floor = floor;
        _client = new SystemOneClient(_recorder, endpoint, model, timeoutMs, _ => key);
    }

    /// <summary>Input tokens the provider reported so far.</summary>
    public long InputTokens => _recorder.InputTokens;

    /// <summary>Output tokens the provider reported so far.</summary>
    public long OutputTokens => _recorder.OutputTokens;

    /// <summary>Spend the provider reported so far, in dollars.</summary>
    public double CostUsd => _recorder.CostUsd;

    /// <summary>The HTTP statuses of failed calls, for the outage report.</summary>
    public IReadOnlyCollection<int> FailedStatuses => _recorder.FailedStatuses;

    /// <summary>Builds the question for a case: one narrow two-option choice about one rule.</summary>
    /// <remarks>
    /// The answer that would let a defect pass is the second option, because Jev leans toward
    /// the first. For a home document that is <c>stated</c>; for a retired rule it is <c>not-stated</c>.
    /// </remarks>
    public static ArbiterRule BuildQuestion(ProseCase item)
    {
        bool retired = item.Kind is ProseCaseKind.RetiredAbsent or ProseCaseKind.RetiredControl;
        string instructions = "Does the document tell its reader the rule below? The document covers many other things; "
            + "judge only whether this one rule is given anywhere in it, in any wording.\n\nRule: " + item.Statement;
        Dictionary<string, string> criteria = new(StringComparer.Ordinal);
        string stated = "Somewhere in the document, in its own words, it gives this rule as something its reader must do, "
            + "must not do, or can rely on. The wording may differ, the reader may be named differently (you, a worker, a dev "
            + "agent), and the parts of the rule may be spread over nearby sentences or a list.";
        string notStated = "Nowhere does the document give this rule. It is silent on the subject, or mentions the subject "
            + "without this rule, or gives a different, narrower or opposite rule, or only points to another file for it."
            + (item.Contrast is null ? string.Empty : " A document that gives the rule that replaced it, \"" + item.Contrast + "\", does not give this one.");
        if (retired)
        {
            criteria[StatedAnswer] = stated;
            criteria[NotStatedAnswer] = notStated;
        }
        else
        {
            criteria[NotStatedAnswer] = notStated;
            criteria[StatedAnswer] = stated;
        }

        Dictionary<string, string> effects = new(StringComparer.Ordinal)
        {
            [StatedAnswer] = RuleEffects.Allow,
            [NotStatedAnswer] = RuleEffects.Allow,
        };
        RuleAsk ask = new("choice", new Dictionary<string, string>(StringComparer.Ordinal) { ["document"] = DocumentFact }, criteria);
        return new ArbiterRule(QuestionId, "prose", instructions, [.. criteria.Keys], effects, Ask: ask);
    }

    /// <summary>Whether the case's request fits the model's budget.</summary>
    public bool FitsBudget(ProseCase item)
    {
        SystemOneBuiltRequest built = SystemOneContracts.BuildRequest([BuildQuestion(item)], Facts(item), _model, null);
        return !StateBudget.IsOverBudget(
            _model, built.StateTokens, built.LongestQuestionTokens, built.TotalRequestTokens, built.BodyBytes, 1);
    }

    /// <summary>Asks one case and classifies the answer.</summary>
    public async Task<ProseResult> GradeAsync(ProseCase item, CancellationToken cancellationToken)
    {
        ArbiterRule question = BuildQuestion(item);
        ArbiterStep1BatchResult result = await _client.AskStep1Async([question], Facts(item), null, cancellationToken);
        if (!result.RequestSent)
        {
            return new ProseResult(item, ProseOutcome.OverBudget, null, null, "not sent: over the model's budget");
        }

        if (result.ScoresByRule is null || !result.ScoresByRule.TryGetValue(QuestionId, out ArbiterStep1Score? score)
            || !result.AnswersByRule.TryGetValue(QuestionId, out string? answer)
            || !question.Answers.Contains(answer, StringComparer.Ordinal))
        {
            return new ProseResult(item, ProseOutcome.Outage, null, null, "no readable answer");
        }

        double? confidence = score.Confidence;
        bool expected = string.Equals(answer, item.Expected, StringComparison.Ordinal);
        bool confident = confidence is double value && value >= _floor;
        ProseOutcome outcome = (expected, confident) switch
        {
            (true, true) => ProseOutcome.Pass,
            (true, false) => ProseOutcome.Warning,
            (false, true) => ProseOutcome.Wrong,
            _ => ProseOutcome.Unsure,
        };
        return new ProseResult(item, outcome, answer, confidence, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _client.Dispose();
        _recorder.Dispose();
    }

    private static ArbiterFactSet Facts(ProseCase item) =>
        new(new Dictionary<string, ArbiterFact>(StringComparer.Ordinal)
        {
            [DocumentFact] = new ArbiterFact(item.Text, ArbiterFactLabel.Asserted),
        });

    /// <summary>Reads each response's usage and cost on the way through, since the client's result carries no cost.</summary>
    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly Lock _gate = new();
        private readonly List<int> _failedStatuses = [];
        private long _inputTokens;
        private long _outputTokens;
        private double _costUsd;

        public long InputTokens
        {
            get { lock (_gate) { return _inputTokens; } }
        }

        public long OutputTokens
        {
            get { lock (_gate) { return _outputTokens; } }
        }

        public double CostUsd
        {
            get { lock (_gate) { return _costUsd; } }
        }

        public IReadOnlyCollection<int> FailedStatuses
        {
            get { lock (_gate) { return [.. _failedStatuses]; } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                lock (_gate)
                {
                    _failedStatuses.Add((int)response.StatusCode);
                }

                return response;
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Record(body);
            StringContent copy = new(body, Encoding.UTF8, "application/json");
            response.Content.Dispose();
            response.Content = copy;
            return response;
        }

        private void Record(string body)
        {
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                if (!json.RootElement.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                lock (_gate)
                {
                    _inputTokens += ReadLong(usage, "input_tokens");
                    _outputTokens += ReadLong(usage, "output_tokens");
                    if (usage.TryGetProperty("cost", out JsonElement cost) && cost.ValueKind == JsonValueKind.Number)
                    {
                        _costUsd += cost.GetDouble();
                    }
                }
            }
            catch (JsonException)
            {
                // The client reports the unreadable body as no answer; there is nothing to count.
            }
        }

        private static long ReadLong(JsonElement parent, string name) =>
            parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt64()
                : 0;
    }
}

/// <summary>Formats grading results as a per-rule report.</summary>
internal static class ProseReport
{
    /// <summary>One line per result that did not pass, then a per-rule summary.</summary>
    public static string Format(IReadOnlyList<ProseResult> results)
    {
        StringBuilder report = new();
        foreach (ProseResult result in results.Where(item => item.Outcome != ProseOutcome.Pass)
                     .OrderBy(item => item.Case.RuleId, StringComparer.Ordinal))
        {
            string note = result.Case.Note is null ? string.Empty : $" ({result.Case.Note})";
            string detail = result.Detail is null ? string.Empty : $" -- {result.Detail}";
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{result.Outcome.ToString().ToUpperInvariant(),-10} {result.Case.RuleId} [{result.Case.Kind}] {result.Case.Path} expected {result.Case.Expected}, got {result.Answer ?? "-"} at {Confidence(result.Confidence)}{note}{detail}");
        }

        report.AppendLine();
        report.AppendLine("rule                                    stated  controls  retired  retired-ctl  min-conf");
        foreach (IGrouping<string, ProseResult> rule in results.GroupBy(item => item.Case.RuleId).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            string lowest = Confidence(rule.Where(item => item.Outcome == ProseOutcome.Pass).Select(item => item.Confidence).Min());
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{rule.Key,-40}{Tally(rule, ProseCaseKind.Stated),-8}{Tally(rule, ProseCaseKind.Control),-10}{Tally(rule, ProseCaseKind.RetiredAbsent),-9}{Tally(rule, ProseCaseKind.RetiredControl),-13}{lowest}");
        }

        return report.ToString();
    }

    private static string Tally(IEnumerable<ProseResult> results, ProseCaseKind kind)
    {
        List<ProseResult> matching = results.Where(item => item.Case.Kind == kind).ToList();
        return matching.Count == 0
            ? "-"
            : $"{matching.Count(item => item.Outcome == ProseOutcome.Pass)}/{matching.Count}";
    }

    private static string Confidence(double? value) =>
        value is double number ? number.ToString("0.000", CultureInfo.InvariantCulture) : "-";
}
