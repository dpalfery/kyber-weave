using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 1.7: one batched step-1 <c>systemone</c> call per trigger,
/// provider <c>none</c>, and state budgets. RED: <c>SystemOneClient</c> does not exist yet.
/// </summary>
public sealed class ArbiterSystemOneClientTests
{
    private const string ChoiceRuleId = "KW-ARB-SCOPE-002";
    private const string ScoreRuleId = "KW-ARB-SCORE-001";
    private const string NoulRuleId = "KW-ARB-LENS-001";
    private const string JevModel = "jev-1.13.0";
    private static readonly Uri RemoteEndpoint = new("https://api.typesafe.ai/v1");

    private static ArbiterFactSet Facts() => new ArbiterFactSet()
        .With("plan.task.text", "Migrate the login flow.", ArbiterFactLabel.Asserted)
        .With("delegation.prompt", "Migrate the login flow to the new SDK.", ArbiterFactLabel.Asserted)
        .With("roster.descriptions", "csharp-dev owns C#.", ArbiterFactLabel.Derived)
        .With("delegation.target", "csharp-dev", ArbiterFactLabel.Asserted)
        .With("lens.applicability", "Applies to C# changes.", ArbiterFactLabel.Asserted)
        .With("review.diff-summary", "+1 line in login.cs", ArbiterFactLabel.Derived)
        // Declared by no rule state: must never leave the machine (R9).
        .With("secret.password", "hunter2-undeclared", ArbiterFactLabel.Asserted);

    private static ArbiterRule ChoiceRule(double? confidenceAtLeast = 0.8) => new(
        ChoiceRuleId,
        "delegate",
        "Does the delegation ask for work the plan task does not describe?",
        ["within-task", "adds-work", "unrelated"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["within-task"] = RuleEffects.Allow,
            ["adds-work"] = RuleEffects.Escalate,
            ["unrelated"] = RuleEffects.Escalate,
        },
        Ask: new RuleAsk(
            "choice",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["task"] = "plan.task.text",
                ["delegation"] = "delegation.prompt",
            },
            Criteria: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["within-task"] = "Every piece of work the delegation asks for is described by the task.",
                ["adds-work"] = "The delegation asks for work the task does not describe.",
                ["unrelated"] = "The delegation does not concern this task.",
            },
            ConfidenceAtLeast: confidenceAtLeast,
            TunedFor: [JevModel]));

    private static ArbiterRule ScoreRule(double? confidenceAtLeast = 0.8) => new(
        ScoreRuleId,
        "delegate",
        "How strongly does the evidence support the claim?",
        ["low", "high"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["low"] = RuleEffects.Allow,
            ["high"] = RuleEffects.Verify,
        },
        Ask: new RuleAsk(
            "score",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["claim"] = "plan.task.text",
            },
            Levels: ["low", "high"],
            ConfidenceAtLeast: confidenceAtLeast,
            TunedFor: [JevModel]));

    private static ArbiterRule NoulRule(double? probabilityBelow = 0.1) => new(
        NoulRuleId,
        "lens.spawn",
        "Does the lens apply to this change?",
        ["applies", "not-applicable"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["applies"] = RuleEffects.Allow,
            ["not-applicable"] = RuleEffects.Skip,
        },
        Ask: new RuleAsk(
            "noul",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["applicability"] = "lens.applicability",
                ["diff"] = "review.diff-summary",
            },
            Question: "Does the lens apply to this change?",
            ProbabilityBelow: probabilityBelow,
            TunedFor: [JevModel]));

    private static SystemOneClient Client(
        StubHandler handler,
        Uri? endpoint = null,
        string model = JevModel,
        int timeoutMs = 5000,
        Func<Uri, string?>? keyResolver = null) =>
        new(handler, endpoint ?? RemoteEndpoint, model, timeoutMs, keyResolver);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string ChoiceBody(
        string choice = "within-task",
        double? confidence = 0.85,
        bool withUsage = true) =>
        "{\"model\":\"" + JevModel + "\",\"answers\":{\"" + ChoiceRuleId + "\":" +
        "{\"type\":\"choice\",\"choice\":\"" + choice + "\"," +
        "\"probabilities\":{\"within-task\":0.88,\"adds-work\":0.1,\"unrelated\":0.02}" +
        (confidence.HasValue
            ? ",\"confidence\":" + confidence.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty) +
        "}}" + (withUsage ? ",\"usage\":{\"input_tokens\":307,\"output_tokens\":20}" : string.Empty) + "}";

    // ---- request shape ----

    [Fact]
    public async Task Request_ChoiceQuestion_KeyedByRuleIdWithNonEmptyInstructions()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.True(result.RequestSent);
        Assert.Equal("within-task", result.AnswersByRule[ChoiceRuleId]);
        Assert.Single(handler.Requests);
        StubHandler.CapturedRequest request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", request.Uri!.AbsoluteUri);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal(JevModel, body.RootElement.GetProperty("model").GetString());
        JsonElement questions = body.RootElement.GetProperty("questions");
        Assert.True(questions.TryGetProperty(ChoiceRuleId, out JsonElement question));
        Assert.Equal("choice", question.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(question.GetProperty("instructions").GetString()));
        Assert.Equal(
            "Every piece of work the delegation asks for is described by the task.",
            question.GetProperty("criteria").GetProperty("within-task").GetString());
    }

    [Fact]
    public async Task Request_ScoreQuestion_SendsLevelsAsCriteriaArray()
    {
        const string json = "{\"model\":\"" + JevModel + "\",\"answers\":{\"" + ScoreRuleId + "\":" +
            "{\"type\":\"score\",\"score\":1.0,\"legend\":{\"0\":\"low\",\"1\":\"high\"}," +
            "\"probabilities\":{\"1\":0.9},\"confidence\":0.92}}," +
            "\"usage\":{\"input_tokens\":10,\"output_tokens\":2}}";
        using StubHandler handler = new(JsonResponse(json));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ScoreRule()], Facts());

        Assert.Equal("high", result.AnswersByRule[ScoreRuleId]);
        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body);
        JsonElement question = body.RootElement.GetProperty("questions").GetProperty(ScoreRuleId);
        Assert.Equal("score", question.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(question.GetProperty("instructions").GetString()));
        string[] levels = question.GetProperty("criteria").EnumerateArray()
            .Select(level => level.GetString()!).ToArray();
        Assert.Equal(["low", "high"], levels);
    }

    [Fact]
    public async Task Request_NoulQuestion_SendsTypeAndInstructions()
    {
        const string json = "{\"model\":\"" + JevModel + "\",\"answers\":{\"" + NoulRuleId + "\":" +
            "{\"type\":\"noul\",\"noul\":0.5}}}";
        using StubHandler handler = new(JsonResponse(json));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([NoulRule()], Facts());

        Assert.Equal("applies", result.AnswersByRule[NoulRuleId]);
        using JsonDocument body = JsonDocument.Parse(handler.Requests[0].Body);
        JsonElement question = body.RootElement.GetProperty("questions").GetProperty(NoulRuleId);
        Assert.Equal("noul", question.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(question.GetProperty("instructions").GetString()));
    }

    [Fact]
    public async Task Request_State_SendsOnlyDeclaredFacts()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler);

        await client.AskStep1Async([ChoiceRule()], Facts());

        string body = handler.Requests[0].Body;
        Assert.Contains("Migrate the login flow.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-undeclared", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.password", body, StringComparison.Ordinal);
    }

    // ---- transport ----

    [Fact]
    public async Task Transport_RemoteEndpointWithKey_SendsBearerAuthorization()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(
            handler, keyResolver: _ => "remote-key");

        await client.AskStep1Async([ChoiceRule()], Facts());

        AuthenticationHeaderValue? authorization = handler.Requests[0].Authorization;
        Assert.NotNull(authorization);
        Assert.Equal("Bearer", authorization.Scheme);
        Assert.Equal("remote-key", authorization.Parameter);
    }

    [Fact]
    public async Task Transport_LoopbackEndpoint_SendsNoAuthorization()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(
            handler,
            endpoint: new Uri("http://localhost:11434/v1"),
            model: "nimble",
            keyResolver: _ => "must-never-be-sent");

        await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("http://localhost:11434/v1/systemone", handler.Requests[0].Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task Transport_NoKeyForOrigin_SendsNoAuthorization()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler, keyResolver: _ => null);

        await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Null(handler.Requests[0].Authorization);
    }

    // ---- retries and errors ----

    [Theory]
    [InlineData(429)]
    [InlineData(529)]
    public async Task Retry_RetryableStatusThenSuccess_AnswersFromRetry(int statusCode)
    {
        HttpResponseMessage retryable = new((HttpStatusCode)statusCode)
        {
            Content = new StringContent("{\"error\":\"busy\"}", Encoding.UTF8, "application/json"),
        };
        retryable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
        using StubHandler handler = new(retryable, JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("within-task", result.AnswersByRule[ChoiceRuleId]);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task Errors_NonRetryableStatus_AnswersUndecidable(int statusCode)
    {
        using StubHandler handler = new(JsonResponse("{\"error\":\"no\"}", (HttpStatusCode)statusCode));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    [Fact]
    public async Task Errors_Timeout_AnswersUndecidable()
    {
        using StubHandler handler = new(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return JsonResponse(ChoiceBody());
        });
        using SystemOneClient client = Client(handler, timeoutMs: 100);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    [Fact]
    public async Task Errors_UnreadableBody_AnswersUndecidable()
    {
        using StubHandler handler = new(JsonResponse("this is not json"));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    // ---- budgets: over budget sends zero requests ----

    [Fact]
    public async Task Budget_StateOverModelBudget_SendsZeroRequests()
    {
        string huge = new('x', 9000);
        ArbiterFactSet facts = new ArbiterFactSet()
            .With("plan.task.text", huge, ArbiterFactLabel.Asserted)
            .With("delegation.prompt", "tiny", ArbiterFactLabel.Asserted);
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler, model: "tev1");

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], facts);

        Assert.Empty(handler.Requests);
        Assert.False(result.RequestSent);
        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    [Fact]
    public async Task Budget_UnknownModel_GetsSmallestBudget()
    {
        string huge = new('x', 9000);
        ArbiterFactSet facts = new ArbiterFactSet()
            .With("plan.task.text", huge, ArbiterFactLabel.Asserted)
            .With("delegation.prompt", "tiny", ArbiterFactLabel.Asserted);
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler, model: "mystery-9");

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], facts);

        Assert.Empty(handler.Requests);
        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    [Fact]
    public async Task Budget_MoreThan64Questions_SendsZeroRequests()
    {
        List<ArbiterRule> rules = [];
        for (int index = 0; index < 65; index++)
        {
            ArbiterRule seed = ChoiceRule();
            rules.Add(seed with { Id = $"KW-ARB-SCOPE-{1000 + index}" });
        }
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async(rules, Facts());

        Assert.Empty(handler.Requests);
        Assert.False(result.RequestSent);
        Assert.All(result.AnswersByRule.Values, answer => Assert.Equal(RuleEngine.Undecidable, answer));
    }

    [Fact]
    public async Task Budget_UnderBudget_SendsOneRequest()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler, model: "mystery-9");

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Single(handler.Requests);
        Assert.True(result.RequestSent);
    }

    // ---- responses: optional fields, thresholds, recording ----

    [Fact]
    public async Task Response_ChoiceWithoutConfidenceOrUsage_ParsesWhenRuleHasNoFloor()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody(confidence: null, withUsage: false)));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule(null)], Facts());

        Assert.Equal("within-task", result.AnswersByRule[ChoiceRuleId]);
        Assert.Equal(JevModel, result.Model);
        Assert.Null(result.Usage);
    }

    [Fact]
    public async Task Response_UsageAndModel_AreRecorded()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody()));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Equal(JevModel, result.Model);
        Assert.NotNull(result.Usage);
        Assert.Equal(307, result.Usage.InputTokens);
        Assert.Equal(20, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task Threshold_ChoiceBelowConfidenceFloor_AnswersUndecidable()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody(confidence: 0.5)));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule(0.8)], Facts());

        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    [Fact]
    public async Task Threshold_ChoiceWithoutConfidenceButWithFloor_AnswersUndecidable()
    {
        using StubHandler handler = new(JsonResponse(ChoiceBody(confidence: null)));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule(0.8)], Facts());

        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    [Theory]
    [InlineData(0.05, "not-applicable")]
    [InlineData(0.5, "applies")]
    public async Task Threshold_NoulProbabilityFloor_AppliesRuleModel(double probability, string expected)
    {
        string json = "{\"model\":\"" + JevModel + "\",\"answers\":{\"" + NoulRuleId + "\":" +
            "{\"type\":\"noul\",\"noul\":" + probability.ToString(CultureInfo.InvariantCulture) + "}}}";
        using StubHandler handler = new(JsonResponse(json));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([NoulRule(0.1)], Facts());

        Assert.Equal(expected, result.AnswersByRule[NoulRuleId]);
    }

    [Fact]
    public async Task Threshold_ScoreBelowConfidenceFloor_AnswersUndecidable()
    {
        const string json = "{\"model\":\"" + JevModel + "\",\"answers\":{\"" + ScoreRuleId + "\":" +
            "{\"type\":\"score\",\"score\":1.0,\"legend\":{\"0\":\"low\",\"1\":\"high\"}," +
            "\"probabilities\":{\"1\":0.9},\"confidence\":0.2}}}";
        using StubHandler handler = new(JsonResponse(json));
        using SystemOneClient client = Client(handler);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ScoreRule(0.8)], Facts());

        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ScoreRuleId]);
    }

    // ---- provider none ----

    [Fact]
    public async Task NoneProvider_MarksStep1RulesNotEvaluated()
    {
        NoneProvider provider = new();

        ArbiterStep1BatchResult result = await provider.AskStep1Async(
            [ChoiceRule(), NoulRule()], Facts());

        Assert.False(result.RequestSent);
        Assert.Equal(ArbiterStep1BatchResult.NotEvaluated, result.AnswersByRule[ChoiceRuleId]);
        Assert.Equal(ArbiterStep1BatchResult.NotEvaluated, result.AnswersByRule[NoulRuleId]);
        Assert.NotEqual(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
    }

    // ---- no leaks ----

    [Fact]
    public async Task Security_SentinelKeyAppearsInNoOutput()
    {
        const string sentinel = "SENTINEL-ARB-KEY-7Q9Z";
        using StubHandler handler = new(JsonResponse("{\"error\":\"denied\"}", HttpStatusCode.Unauthorized));
        using SystemOneClient client = Client(handler, keyResolver: _ => sentinel);

        ArbiterStep1BatchResult result = await client.AskStep1Async([ChoiceRule()], Facts());

        Assert.Equal(RuleEngine.Undecidable, result.AnswersByRule[ChoiceRuleId]);
        Assert.DoesNotContain(sentinel, client.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, handler.Requests[0].Body, StringComparison.Ordinal);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _responder;

        public StubHandler(params HttpResponseMessage[] responses)
        {
            foreach (HttpResponseMessage response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Requests.Add(new CapturedRequest(
                request.Method, request.RequestUri, body, request.Headers.Authorization));
            if (_responder is not null)
            {
                return await _responder(request, cancellationToken).ConfigureAwait(false);
            }

            if (_responses.Count == 0)
            {
                throw new Xunit.Sdk.XunitException("The systemone client sent an unexpected request.");
            }

            return _responses.Dequeue();
        }

        public sealed record CapturedRequest(
            HttpMethod Method,
            Uri? Uri,
            string Body,
            AuthenticationHeaderValue? Authorization);
    }
}
