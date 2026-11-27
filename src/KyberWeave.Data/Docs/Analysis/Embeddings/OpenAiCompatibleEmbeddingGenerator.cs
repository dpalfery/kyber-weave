using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using KyberWeave.Contracts.Repositories;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Model;
using KyberWeave.Core.Networking;

namespace KyberWeave.Core.Docs.Analysis.Embeddings;

/// <summary>Calls an OpenAI-compatible embeddings endpoint that is confined to loopback.</summary>
public sealed class OpenAiCompatibleEmbeddingGenerator : IEmbeddingGenerator, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<string, IReadOnlyList<IPAddress>> _resolveHost;
    private readonly Func<string, string?> _readEnvironment;

    private readonly TimeProvider _clock;

    /// <summary>Creates a generator over a transport that pins its connections to loopback.</summary>
    /// <param name="transport">The HTTP transport and name resolution the generator calls the endpoint through.</param>
    /// <param name="environment">Reads the variable that holds the endpoint's bearer token.</param>
    /// <param name="clock">Times the per-call limit.</param>
    public OpenAiCompatibleEmbeddingGenerator(IEmbeddingTransport transport, IProcessEnvironment environment, TimeProvider clock)
        : this(HandlerOf(transport), transport.ResolveHost, EnvironmentOf(environment).GetVariable, clock)
    {
    }

    /// <remarks>
    /// The handler and resolver come from <see cref="IEmbeddingTransport"/> in production and from a script in tests,
    /// so the transport boundary stays deterministic.
    /// </remarks>
    internal OpenAiCompatibleEmbeddingGenerator(
        HttpMessageHandler handler,
        Func<string, IReadOnlyList<IPAddress>> resolveHost,
        Func<string, string?> readEnvironment,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _resolveHost = resolveHost ?? throw new ArgumentNullException(nameof(resolveHost));
        _readEnvironment = readEnvironment ?? throw new ArgumentNullException(nameof(readEnvironment));
        _clock = clock ?? TimeProvider.System;
        _client = new HttpClient(handler, disposeHandler: true)
        {
            // HttpClient's default 100s timeout would silently clamp config.TimeoutSeconds.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static HttpMessageHandler HandlerOf(IEmbeddingTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        return transport.CreateHandler();
    }

    private static IProcessEnvironment EnvironmentOf(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment;
    }

    public string GetProviderFingerprint(DocsAnalysisEmbeddingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Uri endpoint = RequireEndpoint(config);
        string identity = $"openai-compatible/v1\n{endpoint.AbsoluteUri}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public EmbeddingGenerationResult Generate(
        IReadOnlyCollection<EmbeddingCacheKey> keys,
        IReadOnlyCollection<string> inputs,
        DocsAnalysisEmbeddingConfig config)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(config);

        EmbeddingCacheKey[] orderedKeys = keys.ToArray();
        string[] orderedInputs = inputs.ToArray();
        if (orderedKeys.Length != orderedInputs.Length)
            throw new ArgumentException("Embedding keys and inputs must have the same count.", nameof(inputs));
        if (orderedKeys.Length == 0)
            return new EmbeddingGenerationResult([], EmbeddingUsage.None);
        if (config.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "Embedding batch size must be positive.");
        if (config.TimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "Embedding timeout must be positive.");

        Uri endpoint = RequireEndpoint(config);
        EnsureLoopback(endpoint);
        string model = string.IsNullOrWhiteSpace(config.Model)
            ? throw new ArgumentException("An embedding model is required.", nameof(config))
            : config.Model;
        string? bearerToken = string.IsNullOrWhiteSpace(config.ApiKeyEnv)
            ? null
            : _readEnvironment(config.ApiKeyEnv);

        List<StoredEmbedding> result = new List<StoredEmbedding>(orderedKeys.Length);
        int promptTokens = 0;
        int totalTokens = 0;
        for (int offset = 0; offset < orderedInputs.Length; offset += config.BatchSize)
        {
            int count = Math.Min(config.BatchSize, orderedInputs.Length - offset);
            string[] batchInputs = orderedInputs.AsSpan(offset, count).ToArray();
            ParsedBatch parsed = SendBatchAsync(
                    endpoint,
                    model,
                    config.Dimensions,
                    batchInputs,
                    bearerToken,
                    config.TimeoutSeconds)
                .GetAwaiter()
                .GetResult();
            for (int index = 0; index < parsed.Vectors.Count; index++)
                result.Add(new StoredEmbedding(orderedKeys[offset + index], parsed.Vectors[index]));
            promptTokens = checked(promptTokens + parsed.Usage.PromptTokens);
            totalTokens = checked(totalTokens + parsed.Usage.TotalTokens);
        }

        return new EmbeddingGenerationResult(result, new EmbeddingUsage(promptTokens, totalTokens));
    }

    private async Task<ParsedBatch> SendBatchAsync(
        Uri endpoint,
        string model,
        int? dimensions,
        IReadOnlyList<string> inputs,
        string? bearerToken,
        int timeoutSeconds)
    {
        using HttpRequestMessage request = CreateRequest(endpoint, model, dimensions, inputs, bearerToken);
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), _clock);
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token).ConfigureAwait(false);
        if (IsRedirect(response.StatusCode))
            throw new InvalidOperationException("The local embedding endpoint returned a redirect; redirects are disabled.");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The local embedding endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellation.Token)
            .ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellation.Token).ConfigureAwait(false);
        return ParseBatch(json.RootElement, inputs.Count, dimensions);
    }

    public void Dispose() => _client.Dispose();

    private static HttpRequestMessage CreateRequest(
        Uri endpoint,
        string model,
        int? dimensions,
        IReadOnlyList<string> inputs,
        string? bearerToken)
    {
        Dictionary<string, object?> payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["input"] = inputs,
            ["model"] = model,
            ["encoding_format"] = "float"
        };
        if (dimensions is not null) payload["dimensions"] = dimensions.Value;

        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return request;
    }

    private static ParsedBatch ParseBatch(JsonElement root, int expectedCount, int? configuredDimensions)
    {
        if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The embedding response must contain a data array with complete indices.");

        IReadOnlyList<float>?[] vectors = new IReadOnlyList<float>?[expectedCount];
        int? dimensions = configuredDimensions;
        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out JsonElement indexValue)
                || !indexValue.TryGetInt32(out int index)
                || index < 0
                || index >= expectedCount
                || vectors[index] is not null)
            {
                throw new InvalidDataException("Embedding response index values must be unique and in range.");
            }

            if (!item.TryGetProperty("embedding", out JsonElement embedding)
                || embedding.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Each embedding response item must contain a finite vector.");
            }

            float[] raw = embedding.EnumerateArray().Select(ReadFiniteFloat).ToArray();
            dimensions ??= raw.Length;
            if (raw.Length == 0 || raw.Length != dimensions.Value)
                throw new InvalidDataException("Embedding vector dimensions must be non-zero and consistent.");
            vectors[index] = Normalize(raw);
        }

        if (vectors.Any(vector => vector is null))
            throw new InvalidDataException("Embedding response index values must be complete.");

        return new ParsedBatch(
            vectors.Select(vector => vector
                ?? throw new InvalidDataException("Embedding response index values must be complete.")).ToArray(),
            ReadUsage(root));
    }

    private static float ReadFiniteFloat(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number)
            || !double.IsFinite(number) || number > float.MaxValue || number < -float.MaxValue)
        {
            throw new InvalidDataException("Embedding vectors must contain only finite values.");
        }

        return (float)number;
    }

    private static IReadOnlyList<float> Normalize(IReadOnlyList<float> vector)
    {
        double squaredNorm = vector.Sum(value => (double)value * value);
        if (!double.IsFinite(squaredNorm) || squaredNorm <= 0)
            throw new InvalidDataException("Embedding vectors must have a finite, non-zero norm.");

        double norm = Math.Sqrt(squaredNorm);
        return vector.Select(value => (float)(value / norm)).ToArray();
    }

    private static EmbeddingUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
            return EmbeddingUsage.None;

        return new EmbeddingUsage(
            ReadNonNegativeInt(usage, "prompt_tokens"),
            ReadNonNegativeInt(usage, "total_tokens"));
    }

    private static int ReadNonNegativeInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value)) return 0;
        if (!value.TryGetInt32(out int count) || count < 0)
            throw new InvalidDataException($"Embedding usage '{propertyName}' must be a non-negative integer.");
        return count;
    }

    private void EnsureLoopback(Uri endpoint)
    {
        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = _resolveHost(endpoint.DnsSafeHost);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException("The embedding endpoint host could not be resolved to loopback.", ex);
        }

        if (addresses.Count == 0 || addresses.Any(address => !LoopbackAddress.IsLoopback(address)))
            throw new InvalidOperationException("The embedding endpoint must resolve only to loopback addresses.");
    }

    private static Uri RequireEndpoint(DocsAnalysisEmbeddingConfig config)
    {
        Uri endpoint = config.Endpoint
            ?? throw new ArgumentException("An embedding endpoint is required.", nameof(config));
        if (!endpoint.IsAbsoluteUri
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The embedding endpoint must be an absolute HTTP(S) URI.", nameof(config));
        }

        return endpoint;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        (int)statusCode is >= 300 and <= 399;

    private sealed record ParsedBatch(
        IReadOnlyList<IReadOnlyList<float>> Vectors,
        EmbeddingUsage Usage);
}
