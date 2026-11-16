using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Rules;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>
/// The <c>systemone</c> client: one batched step-1 call per trigger to TypeSafe
/// or a local Ollama, or no call when over budget. Hand-written on the BCL
/// <see cref="HttpClient"/> because TypeSafe ships no .NET SDK.
/// </summary>
/// <remarks>
/// The key travels only in the <c>Authorization</c> header and never appears
/// in an output, an exception message or <c>ToString</c>: error paths return
/// <c>undecidable</c> answers rather than throwing, and this type adds no
/// <c>ToString</c> that could repeat it. A loopback endpoint never sends
/// authorization, since a local origin holds no key.
/// </remarks>
public sealed class SystemOneClient : IArbiterProvider, IDisposable
{
    private const int MaxAttempts = 5;

    /// <summary>The largest provider response the hook will materialize, in bytes.</summary>
    private const int MaxResponseBytes = 1 << 20;

    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly int _timeoutMs;
    private readonly Func<Uri, string?>? _keyResolver;
    private readonly Func<ArbiterRule, ArbiterFactSet, string?>? _instructionsResolver;

    /// <summary>Creates a client over an injected handler, so tests stay offline.</summary>
    /// <param name="handler">The transport; owned and disposed with the client.</param>
    /// <param name="endpoint">The provider base URL; <c>/systemone</c> is appended.</param>
    /// <param name="model">The answering model, which also selects the budget.</param>
    /// <param name="timeoutMs">The per-call budget covering retries.</param>
    /// <param name="keyResolver">Returns the key for the endpoint's origin, or null.</param>
    /// <param name="instructionsResolver">Resolves a rule's instructions from the facts of the trigger being asked.</param>
    public SystemOneClient(
        HttpMessageHandler handler,
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, string?>? keyResolver = null,
        Func<ArbiterRule, ArbiterFactSet, string?>? instructionsResolver = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeoutMs, 0);
        // HttpClient's default 100s timeout would silently clamp timeoutMs.
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _endpoint = endpoint;
        _model = model;
        _timeoutMs = timeoutMs;
        _keyResolver = keyResolver;
        _instructionsResolver = instructionsResolver;
    }

    /// <summary>Creates the client the product runs, with lens instructions wired in.</summary>
    /// <param name="handler">The transport; owned and disposed with the client.</param>
    /// <param name="endpoint">The provider base URL; <c>/systemone</c> is appended.</param>
    /// <param name="model">The answering model, which also selects the budget.</param>
    /// <param name="timeoutMs">The per-call budget covering retries.</param>
    /// <param name="keyResolver">Returns the key for the endpoint's origin, or null.</param>
    public static SystemOneClient CreateForProduct(
        HttpMessageHandler handler,
        Uri endpoint,
        string model,
        int timeoutMs,
        Func<Uri, string?>? keyResolver) =>
        new(handler, endpoint, model, timeoutMs, keyResolver, LensInstructions.Resolve);

    /// <inheritdoc />
    public async Task<ArbiterStep1BatchResult> AskStep1Async(
        IReadOnlyList<ArbiterRule> rules,
        ArbiterFactSet facts,
        IReadOnlyDictionary<string, string>? step0Answers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(facts);

        List<ArbiterRule> askRules = rules
            .Where(rule => rule.Enabled && rule.Ask is not null
                && SystemOneContracts.WhenMatches(rule.Ask, step0Answers))
            .ToList();
        if (askRules.Count == 0)
        {
            return new ArbiterStep1BatchResult(
                new Dictionary<string, string>(StringComparer.Ordinal), null, null, false);
        }

        SystemOneBuiltRequest built = SystemOneContracts.BuildRequest(
            askRules, facts, _model, _instructionsResolver);
        if (StateBudget.IsOverBudget(
            _model,
            built.StateTokens,
            built.LongestQuestionTokens,
            built.TotalRequestTokens,
            built.BodyBytes,
            askRules.Count))
        {
            return UndecidableAll(askRules, null, null, false);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeoutMs);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(_timeoutMs);
        Uri requestUri = SystemOneContracts.RequestUri(_endpoint);

        int attempt = 0;
        while (true)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, requestUri)
            {
                Content = new StringContent(built.BodyJson, Encoding.UTF8, "application/json"),
            };
            // Origin-bound: the key goes out only for a remote endpoint whose
            // origin resolved one. A loopback endpoint therefore sends none.
            string? key = _keyResolver?.Invoke(requestUri);
            if (!requestUri.IsLoopback && !string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return UndecidableAll(askRules, null, null, true);
            }
            catch (HttpRequestException)
            {
                return UndecidableAll(askRules, null, null, true);
            }

            using (response)
            {
                if (response.StatusCode is (HttpStatusCode)429 or (HttpStatusCode)529)
                {
                    TimeSpan delay = RetryDelay(response, attempt);
                    attempt++;
                    if (attempt >= MaxAttempts || DateTimeOffset.UtcNow + delay > deadline)
                    {
                        return UndecidableAll(askRules, null, null, true);
                    }

                    try
                    {
                        await Task.Delay(delay, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return UndecidableAll(askRules, null, null, true);
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return UndecidableAll(askRules, null, null, true);
                }

                if (response.Content.Headers.ContentLength is long declaredLength
                    && declaredLength > MaxResponseBytes)
                {
                    return UndecidableAll(askRules, null, null, true);
                }

                string body;
                try
                {
                    using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using MemoryStream buffer = new();
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + read > MaxResponseBytes)
                        {
                            return UndecidableAll(askRules, null, null, true);
                        }

                        await buffer.WriteAsync(chunk.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }

                    body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return UndecidableAll(askRules, null, null, true);
                }
                catch (HttpRequestException)
                {
                    return UndecidableAll(askRules, null, null, true);
                }
                catch (IOException)
                {
                    return UndecidableAll(askRules, null, null, true);
                }

                if (!SystemOneContracts.TryParseResponse(body, out SystemOneParsedResponse? parsed)
                    || parsed is null)
                {
                    return UndecidableAll(askRules, null, null, true);
                }

                return ApplyAnswers(askRules, parsed);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private ArbiterStep1BatchResult ApplyAnswers(
        IReadOnlyList<ArbiterRule> askRules, SystemOneParsedResponse parsed)
    {
        Dictionary<string, string> answers = new(StringComparer.Ordinal);
        Dictionary<string, ArbiterStep1Score> scores = new(StringComparer.Ordinal);
        foreach (ArbiterRule rule in askRules)
        {
            string answer = RuleEngine.Undecidable;
            if (parsed.AnswersByRule.TryGetValue(rule.Id, out JsonElement element))
            {
                answer = ApplyAnswer(rule, element, out ArbiterStep1Score? score);
                if (score is not null)
                {
                    scores[rule.Id] = score;
                }
            }

            answers[rule.Id] = answer;
        }

        ArbiterStep1Usage? usage = parsed.InputTokens is int input && parsed.OutputTokens is int output
            ? new ArbiterStep1Usage(input, output)
            : null;
        return new ArbiterStep1BatchResult(answers, parsed.Model ?? _model, usage, true, scores);
    }

    private static string ApplyAnswer(ArbiterRule rule, JsonElement element, out ArbiterStep1Score? score)
    {
        score = null;
        if (rule.Ask is null)
        {
            return RuleEngine.Undecidable;
        }

        if (string.Equals(rule.Ask.Type, "choice", StringComparison.Ordinal))
        {
            if (!SystemOneContracts.TryReadChoice(element, out string? choice, out double? confidence)
                || choice is null)
            {
                return RuleEngine.Undecidable;
            }

            score = new ArbiterStep1Score(
                SystemOneContracts.ReadProbabilities(element, rule.Answers),
                confidence,
                rule.Ask.ConfidenceAtLeast);

            // Below its floor the answer is undecidable; a missing confidence
            // cannot clear a floor. A wrong valid value is still wrong.
            if (rule.Ask.ConfidenceAtLeast is double floor
                && (confidence is null || confidence < floor))
            {
                return RuleEngine.Undecidable;
            }

            return rule.Answers.Contains(choice, StringComparer.Ordinal)
                ? choice
                : RuleEngine.Undecidable;
        }

        if (string.Equals(rule.Ask.Type, "score", StringComparison.Ordinal))
        {
            if (!SystemOneContracts.TryReadScore(element, out double value, out double? scoreConfidence))
            {
                return RuleEngine.Undecidable;
            }

            string[] levelKeys = rule.Ask.Levels is { } scoredLevels
                ? [.. Enumerable.Range(0, scoredLevels.Count).Select(index => index.ToString(CultureInfo.InvariantCulture))]
                : [];
            score = new ArbiterStep1Score(
                SystemOneContracts.ReadProbabilities(element, levelKeys),
                scoreConfidence,
                rule.Ask.ConfidenceAtLeast);

            if (rule.Ask.ConfidenceAtLeast is double scoreFloor
                && (scoreConfidence is null || scoreConfidence < scoreFloor))
            {
                return RuleEngine.Undecidable;
            }

            string mapped;
            if (rule.Ask.Levels is { Count: > 0 } levels)
            {
                double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
                if (rounded < 0 || rounded > levels.Count - 1)
                {
                    return RuleEngine.Undecidable;
                }

                mapped = levels[(int)rounded];
            }
            else
            {
                mapped = value.ToString(CultureInfo.InvariantCulture);
            }
            return rule.Answers.Contains(mapped, StringComparer.Ordinal)
                ? mapped
                : RuleEngine.Undecidable;
        }

        if (string.Equals(rule.Ask.Type, "noul", StringComparison.Ordinal))
        {
            if (!SystemOneContracts.TryReadNoul(element, out double probability)
                || rule.Answers.Count == 0)
            {
                return RuleEngine.Undecidable;
            }

            // The probability is P(first answer): below the ceiling the rule
            // takes its other answer, as the attached rule model says.
            string first = rule.Answers[0];
            string second = rule.Answers.Count > 1 ? rule.Answers[1] : RuleEngine.Undecidable;
            double ceiling = rule.Ask.ProbabilityBelow ?? 0.5;
            score = new ArbiterStep1Score([probability], null, ceiling);
            return probability < ceiling ? second : first;
        }

        return RuleEngine.Undecidable;
    }

    private static ArbiterStep1BatchResult UndecidableAll(
        IReadOnlyList<ArbiterRule> askRules, string? model, ArbiterStep1Usage? usage, bool requestSent)
    {
        Dictionary<string, string> answers = new(StringComparer.Ordinal);
        foreach (ArbiterRule rule in askRules)
        {
            answers[rule.Id] = RuleEngine.Undecidable;
        }

        return new ArbiterStep1BatchResult(answers, model, usage, requestSent);
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan backoff = TimeSpan.FromMilliseconds(Math.Min(100 * (1 << attempt), 2000));
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            retryAfter = date - DateTimeOffset.UtcNow;
        }

        // Retry-After wins when present; otherwise exponential backoff.
        return retryAfter is { Ticks: >= 0 } present ? present : backoff;
    }
}
