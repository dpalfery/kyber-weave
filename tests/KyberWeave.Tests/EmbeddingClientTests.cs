using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis;
using KyberWeave.Core.Docs.Analysis.Embeddings;
using KyberWeave.Core.Docs.Analysis.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T08 — embedding requests are an optional, local-only optimization. The client must
/// reject any route that could leave loopback, validate provider output before caching
/// it, and make cache safety a precondition of every provider call.
/// </summary>
public sealed class EmbeddingClientTests
{
    [Fact]
    public void Generate_WithOrderedBatchesAndOptionalSettings_MapsByIndexNormalizesAndAggregatesUsage()
    {
        using var handler = new RecordingHandler(
            JsonResponse("""
                {
                  "data": [
                    { "index": 1, "embedding": [0, 5] },
                    { "index": 0, "embedding": [3, 4] }
                  ],
                  "usage": { "prompt_tokens": 7, "total_tokens": 7 }
                }
                """),
            JsonResponse("""
                {
                  "data": [
                    { "index": 0, "embedding": [8, 6] }
                  ],
                  "usage": { "prompt_tokens": 5, "total_tokens": 5 }
                }
                """));
        var environmentReads = new List<string>();
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback),
            name =>
            {
                environmentReads.Add(name);
                return "secret-local-token";
            });
        var config = Config(
            endpoint: "http://localhost:1234/v1/embeddings",
            batchSize: 2,
            dimensions: 2,
            apiKeyEnv: "LOCAL_EMBEDDING_TOKEN");
        var keys = new[] { Key("alpha"), Key("beta"), Key("gamma") };

        var result = generator.Generate(keys, ["first input", "second input", "third input"], config);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(["first input", "second input"], Inputs(handler.Requests[0].Body));
        Assert.Equal(["third input"], Inputs(handler.Requests[1].Body));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(config.Endpoint, request.Uri);
            Assert.Equal("application/json", request.ContentType);
            Assert.Equal("Bearer", request.Authorization?.Scheme);
            Assert.Equal("secret-local-token", request.Authorization?.Parameter);
            Assert.True(request.CanBeCanceled);
            using var json = JsonDocument.Parse(request.Body);
            Assert.Equal("text-embedding-local", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("float", json.RootElement.GetProperty("encoding_format").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("dimensions").GetInt32());
        });
        Assert.Equal(["LOCAL_EMBEDDING_TOKEN"], environmentReads);
        Assert.Equal(keys, result.Embeddings.Select(embedding => embedding.Key));
        AssertVector([0.6f, 0.8f], result.Embeddings[0].Vector);
        AssertVector([0f, 1f], result.Embeddings[1].Vector);
        AssertVector([0.8f, 0.6f], result.Embeddings[2].Vector);
        Assert.Equal(12, result.Usage.PromptTokens);
        Assert.Equal(12, result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData("http://localhost:1234/v1/embeddings", "::1")]
    [InlineData("http://127.9.8.7:1234/v1/embeddings", "127.9.8.7")]
    [InlineData("http://[::1]:1234/v1/embeddings", "::1")]
    [InlineData("http://[::ffff:127.9.8.7]:1234/v1/embeddings", "::ffff:127.9.8.7")]
    public void Generate_WhenEveryResolvedAddressIsLoopback_AcceptsSupportedLoopbackForms(
        string endpoint,
        string resolvedAddress)
    {
        using var handler = new RecordingHandler(JsonResponse("""
            { "data": [{ "index": 0, "embedding": [1, 0] }] }
            """));
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Parse(resolvedAddress)),
            _ => null);

        var result = generator.Generate(
            [Key("loopback")],
            ["local-only input"],
            Config(endpoint: endpoint));

        Assert.Single(result.Embeddings);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Constructor_DisablesHttpClientTimeoutSoConfigTimeoutIsAuthoritative()
    {
        using var handler = new RecordingHandler(JsonResponse("""
            { "data": [{ "index": 0, "embedding": [1, 0] }] }
            """));
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback),
            _ => null);

        var client = typeof(OpenAiCompatibleEmbeddingGenerator)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(generator) as HttpClient;

        Assert.NotNull(client);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void LoadConfig_WhenEndpointIsIpv4Mapped127Slash8_AcceptsItAsLoopback()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml("""
            docs-analysis:
              embeddings:
                mode: prefer
                endpoint: http://[::ffff:127.9.8.7]:1234/v1/embeddings
                model: text-embedding-local
            """);

        Assert.Equal(
            new Uri("http://[::ffff:127.9.8.7]:1234/v1/embeddings"),
            config.DocsAnalysis.Embeddings.Endpoint);
    }

    [Fact]
    public void Generate_WithoutOptionalDimensionsOrApiKey_OmitsBothFromTheRequest()
    {
        using var handler = new RecordingHandler(JsonResponse("""
            {
              "data": [{ "index": 0, "embedding": [1, 0] }]
            }
            """));
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback),
            _ => throw new Xunit.Sdk.XunitException("No environment variable should be read."));
        var config = Config(dimensions: null, apiKeyEnv: null);

        var result = generator.Generate([Key("only", dimensions: null)], ["only input"], config);

        var request = Assert.Single(handler.Requests);
        Assert.Null(request.Authorization);
        using var json = JsonDocument.Parse(request.Body);
        Assert.False(json.RootElement.TryGetProperty("dimensions", out _));
        Assert.Equal(0, result.Usage.PromptTokens);
        Assert.Equal(0, result.Usage.TotalTokens);
    }

    [Fact]
    public void Generate_WhenAnyResolvedAddressIsNotLoopback_RejectsBeforeSending()
    {
        using var handler = new RecordingHandler(JsonResponse("{}"));
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback, IPAddress.Parse("203.0.113.9")),
            _ => null);
        var config = Config(endpoint: "http://embedding.test:1234/v1/embeddings");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate([Key("unsafe")], ["must stay local"], config));

        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("/v1/embeddings")]
    [InlineData("ftp://127.0.0.1/v1/embeddings")]
    [InlineData("http://192.0.2.8/v1/embeddings")]
    public void Generate_WhenEndpointIsNotAbsoluteLoopbackHttp_RejectsBeforeSending(string endpoint)
    {
        using var handler = new RecordingHandler(JsonResponse("{}"));
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Parse("192.0.2.8")),
            _ => null);
        var config = Config(new Uri(endpoint, UriKind.RelativeOrAbsolute));

        var exception = Assert.ThrowsAny<Exception>(() =>
            generator.Generate([Key("unsafe")], ["must stay local"], config));

        Assert.True(
            exception is ArgumentException or InvalidOperationException,
            $"Expected a configuration or loopback-policy failure, got {exception.GetType().Name}: {exception.Message}");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Generate_WhenEndpointRedirects_RejectsTheRedirectWithoutFollowingIt()
    {
        using var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://example.com/v1/embeddings") }
        };
        using var handler = new RecordingHandler(redirect);
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback),
            _ => null);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate([Key("redirect")], ["never forward this text"], Config()));

        Assert.Contains("redirect", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public void Generate_WhenResponseIndicesOrVectorsAreInvalid_RejectsTheWholeBatch(
        string response,
        string expectedMessage)
    {
        using var handler = new RecordingHandler(JsonResponse(response));
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback),
            _ => null);

        var exception = Assert.Throws<InvalidDataException>(() =>
            generator.Generate(
                [Key("left"), Key("right")],
                ["left input", "right input"],
                Config(dimensions: null)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string, string> InvalidResponses() => new()
    {
        {
            """
            { "data": [
              { "index": 0, "embedding": [1, 0] },
              { "index": 0, "embedding": [0, 1] }
            ] }
            """,
            "index"
        },
        {
            """
            { "data": [
              { "index": 0, "embedding": [1, 0] }
            ] }
            """,
            "index"
        },
        {
            """
            { "data": [
              { "index": 0, "embedding": [1, 0] },
              { "index": 2, "embedding": [0, 1] }
            ] }
            """,
            "index"
        },
        {
            """
            { "data": [
              { "index": 0, "embedding": [1, 0] },
              { "index": 1, "embedding": [0, 1, 0] }
            ] }
            """,
            "dimension"
        },
        {
            """
            { "data": [
              { "index": 0, "embedding": [0, 0] },
              { "index": 1, "embedding": [0, 1] }
            ] }
            """,
            "finite"
        },
        {
            """
            { "data": [
              { "index": 0, "embedding": [1e400, 0] },
              { "index": 1, "embedding": [0, 1] }
            ] }
            """,
            "finite"
        }
    };

    [Fact]
    public void Generate_WhenProviderFails_DoesNotExposeTheBearerToken()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("provider rejected credentials", Encoding.UTF8, "text/plain")
        });
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback),
            _ => "do-not-disclose-this-token");

        var exception = Assert.Throws<InvalidOperationException>(() => generator.Generate(
            [Key("failure")],
            ["input"],
            Config(apiKeyEnv: "LOCAL_EMBEDDING_TOKEN")));

        Assert.DoesNotContain("do-not-disclose-this-token", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "do-not-disclose-this-token",
            generator.GetProviderFingerprint(Config(apiKeyEnv: "LOCAL_EMBEDDING_TOKEN")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WhenModeIsOff_NeverReadsCacheOrInvokesProvider()
    {
        var generator = new RecordingGenerator();
        var persistence = new RecordingPersistence(isAvailable: true);
        var coordinator = new EmbeddingCoordinator(generator, persistence);

        var result = coordinator.Resolve(
            [new EmbeddingWorkItem("context", "input")],
            Config(mode: DocsAnalysisEmbeddingMode.Off));

        Assert.Empty(result.Embeddings);
        Assert.Empty(result.Diagnostics.Items);
        Assert.Equal(0, result.CacheHits);
        Assert.Equal(0, result.CacheMisses);
        Assert.Equal(0, persistence.LoadCount);
        Assert.Equal(0, generator.CallCount);
    }

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, Severity.Warning)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, Severity.Error)]
    public void Resolve_WhenPersistenceIsUnsafe_ReportsModeSeverityAndNeverInvokesProvider(
        DocsAnalysisEmbeddingMode mode,
        Severity severity)
    {
        var generator = new RecordingGenerator();
        var persistence = new RecordingPersistence(isAvailable: false);
        var coordinator = new EmbeddingCoordinator(generator, persistence);

        var result = coordinator.Resolve(
            [new EmbeddingWorkItem("context", "input")],
            Config(mode: mode));

        var diagnostic = Assert.Single(result.Diagnostics.Items);
        Assert.Equal(DocumentationAnalyzer.EmbeddingUnavailableRuleCode, diagnostic.Code);
        Assert.Equal(severity, diagnostic.Severity);
        Assert.Empty(result.Embeddings);
        Assert.Equal(0, generator.CallCount);
        Assert.Empty(persistence.Saved);
    }

    [Fact]
    public void Resolve_WithPartialCacheHit_RequestsOnlyMissesAndPreservesInputOrder()
    {
        var generator = new RecordingGenerator(providerFingerprint: "provider-a");
        var config = Config(model: "model-a", dimensions: 2);
        var hitKey = Key(
            "context-hit",
            provider: "provider-a",
            model: "model-a",
            dimensions: 2);
        var cached = new StoredEmbedding(hitKey, [1f, 0f]);
        var persistence = new RecordingPersistence(isAvailable: true, cached);
        var coordinator = new EmbeddingCoordinator(generator, persistence);

        var result = coordinator.Resolve(
            [
                new EmbeddingWorkItem("context-miss-a", "first miss"),
                new EmbeddingWorkItem("context-hit", "cached input"),
                new EmbeddingWorkItem("context-miss-b", "second miss")
            ],
            config);

        Assert.Equal(1, result.CacheHits);
        Assert.Equal(2, result.CacheMisses);
        Assert.Equal(1, generator.CallCount);
        Assert.Equal(["first miss", "second miss"], generator.Inputs);
        Assert.Equal(
            ["context-miss-a", "context-miss-b"],
            generator.Keys.Select(key => key.ContextualHash));
        Assert.All(generator.Keys, key =>
        {
            Assert.Equal("provider-a", key.ProviderFingerprint);
            Assert.Equal("model-a", key.Model);
            Assert.Equal(2, key.Dimensions);
            Assert.Equal("float", key.Encoding);
        });
        Assert.Equal(
            ["context-miss-a", "context-hit", "context-miss-b"],
            result.Embeddings.Select(embedding => embedding.Key.ContextualHash));
        Assert.Equal(2, persistence.Saved.Count);
        Assert.Equal(11, result.Usage.PromptTokens);
        Assert.Equal(13, result.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, Severity.Warning)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, Severity.Error)]
    public void Resolve_WhenProviderFails_ReportsModeSeverityAndDoesNotPersistPartialResults(
        DocsAnalysisEmbeddingMode mode,
        Severity severity)
    {
        var generator = new RecordingGenerator(failure: new InvalidOperationException("endpoint unavailable"));
        var persistence = new RecordingPersistence(isAvailable: true);
        var coordinator = new EmbeddingCoordinator(generator, persistence);

        var result = coordinator.Resolve(
            [new EmbeddingWorkItem("context", "input")],
            Config(mode: mode));

        var diagnostic = Assert.Single(result.Diagnostics.Items);
        Assert.Equal(DocumentationAnalyzer.EmbeddingUnavailableRuleCode, diagnostic.Code);
        Assert.Equal(severity, diagnostic.Severity);
        Assert.Contains("endpoint unavailable", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Embeddings);
        Assert.Empty(persistence.Saved);
    }

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, "json")]
    [InlineData(DocsAnalysisEmbeddingMode.Required, "json")]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, "overflow")]
    [InlineData(DocsAnalysisEmbeddingMode.Required, "overflow")]
    public void Resolve_WhenProviderPayloadIsMalformed_UsesConfiguredFailurePolicy(
        DocsAnalysisEmbeddingMode mode,
        string failureKind)
    {
        Exception failure = failureKind == "json"
            ? new JsonException("malformed provider JSON")
            : new OverflowException("provider usage overflow");
        var generator = new RecordingGenerator(failure: failure);
        var persistence = new RecordingPersistence(isAvailable: true);
        var coordinator = new EmbeddingCoordinator(generator, persistence);

        var result = coordinator.Resolve(
            [new EmbeddingWorkItem("context", "input")],
            Config(mode: mode));

        var diagnostic = Assert.Single(result.Diagnostics.Items);
        Assert.Equal(DocumentationAnalyzer.EmbeddingUnavailableRuleCode, diagnostic.Code);
        Assert.Equal(
            mode == DocsAnalysisEmbeddingMode.Required ? Severity.Error : Severity.Warning,
            diagnostic.Severity);
        Assert.Contains(failure.Message, diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Embeddings);
        Assert.Empty(persistence.Saved);
    }

    [Theory]
    [InlineData(DocsAnalysisEmbeddingMode.Prefer, Severity.Warning)]
    [InlineData(DocsAnalysisEmbeddingMode.Required, Severity.Error)]
    public void Resolve_WhenBearerTokenFormatIsInvalid_ReportsPolicyWithoutDisclosingSecret(
        DocsAnalysisEmbeddingMode mode,
        Severity severity)
    {
        const string secret = "secret-token\nwith-invalid-header-content";
        using var handler = new RecordingHandler(JsonResponse("{}"));
        using var generator = new OpenAiCompatibleEmbeddingGenerator(
            handler,
            ResolveTo(IPAddress.Loopback),
            _ => secret);
        var persistence = new RecordingPersistence(isAvailable: true);
        var coordinator = new EmbeddingCoordinator(generator, persistence);

        var result = coordinator.Resolve(
            [new EmbeddingWorkItem("context", "input")],
            Config(mode: mode, apiKeyEnv: "LOCAL_EMBEDDING_TOKEN"));

        var diagnostic = Assert.Single(result.Diagnostics.Items);
        Assert.Equal(DocumentationAnalyzer.EmbeddingUnavailableRuleCode, diagnostic.Code);
        Assert.Equal(severity, diagnostic.Severity);
        Assert.DoesNotContain(secret, diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(result.Embeddings);
        Assert.Empty(persistence.Saved);
        Assert.Empty(handler.Requests);
    }

    private static DocsAnalysisEmbeddingConfig Config(
        string endpoint = "http://127.0.0.1:1234/v1/embeddings",
        DocsAnalysisEmbeddingMode mode = DocsAnalysisEmbeddingMode.Required,
        string model = "text-embedding-local",
        int? dimensions = 2,
        int batchSize = 64,
        string? apiKeyEnv = null) =>
        Config(new Uri(endpoint, UriKind.Absolute), mode, model, dimensions, batchSize, apiKeyEnv);

    private static DocsAnalysisEmbeddingConfig Config(
        Uri endpoint,
        DocsAnalysisEmbeddingMode mode = DocsAnalysisEmbeddingMode.Required,
        string model = "text-embedding-local",
        int? dimensions = 2,
        int batchSize = 64,
        string? apiKeyEnv = null) =>
        new()
        {
            Mode = mode,
            Endpoint = endpoint,
            Model = model,
            Dimensions = dimensions,
            BatchSize = batchSize,
            TimeoutSeconds = 5,
            ApiKeyEnv = apiKeyEnv
        };

    private static EmbeddingCacheKey Key(
        string contextualHash,
        string provider = "provider",
        string model = "text-embedding-local",
        int? dimensions = 2,
        string encoding = "float") =>
        new(contextualHash, provider, model, dimensions, encoding);

    private static Func<string, IReadOnlyList<IPAddress>> ResolveTo(params IPAddress[] addresses) =>
        _ => addresses;

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string[] Inputs(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("input")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
    }

    private static void AssertVector(IReadOnlyList<float> expected, IReadOnlyList<float> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
            Assert.Equal(expected[index], actual[index], precision: 5);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        string Body,
        string? ContentType,
        AuthenticationHeaderValue? Authorization,
        bool CanBeCanceled);

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.Method,
                request.RequestUri,
                body,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization,
                cancellationToken.CanBeCanceled));
            if (_responses.Count == 0)
                throw new Xunit.Sdk.XunitException("The embedding client sent an unexpected request.");

            return _responses.Dequeue();
        }
    }

    private sealed class RecordingGenerator(
        string providerFingerprint = "provider-a",
        Exception? failure = null) : IEmbeddingGenerator
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<EmbeddingCacheKey> Keys { get; private set; } = [];
        public IReadOnlyList<string> Inputs { get; private set; } = [];

        public string GetProviderFingerprint(DocsAnalysisEmbeddingConfig config) => providerFingerprint;

        public EmbeddingGenerationResult Generate(
            IReadOnlyCollection<EmbeddingCacheKey> keys,
            IReadOnlyCollection<string> inputs,
            DocsAnalysisEmbeddingConfig config)
        {
            CallCount++;
            Keys = keys.ToArray();
            Inputs = inputs.ToArray();
            if (failure is not null) throw failure;

            var embeddings = Keys
                .Select((key, index) => new StoredEmbedding(
                    key,
                    index % 2 == 0 ? [1f, 0f] : [0f, 1f]))
                .ToArray();
            return new EmbeddingGenerationResult(
                embeddings,
                new EmbeddingUsage(PromptTokens: 11, TotalTokens: 13));
        }
    }

    private sealed class RecordingPersistence(
        bool isAvailable,
        params StoredEmbedding[] embeddings) : IAnalysisPersistence
    {
        private readonly Dictionary<EmbeddingCacheKey, StoredEmbedding> _embeddings =
            embeddings.ToDictionary(embedding => embedding.Key);

        public bool IsAvailable { get; } = isAvailable;
        public int LoadCount { get; private set; }
        public List<StoredEmbedding> Saved { get; } = [];

        public IReadOnlyDictionary<string, AnalysisVerdict> LoadVerdicts(
            IReadOnlyCollection<string> candidateIds) =>
            new Dictionary<string, AnalysisVerdict>(StringComparer.Ordinal);

        public IReadOnlyDictionary<EmbeddingCacheKey, StoredEmbedding> LoadEmbeddings(
            IReadOnlyCollection<EmbeddingCacheKey> keys)
        {
            LoadCount++;
            return keys
                .Where(_embeddings.ContainsKey)
                .ToDictionary(key => key, key => _embeddings[key]);
        }

        public void SaveEmbeddings(IReadOnlyCollection<StoredEmbedding> embeddingsToSave)
        {
            foreach (var embedding in embeddingsToSave)
            {
                Saved.Add(embedding);
                _embeddings[embedding.Key] = embedding;
            }
        }
    }
}
