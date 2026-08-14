using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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

    /// <summary>Creates a generator whose connections are resolved and pinned to loopback.</summary>
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient owns and disposes the handler supplied by CreateLocalOnlyHandler.")]
    public OpenAiCompatibleEmbeddingGenerator()
        : this(CreateLocalOnlyHandler(), ResolveHost, Environment.GetEnvironmentVariable)
    {
    }

    /// <remarks>
    /// The injectable handler and resolvers keep the transport boundary deterministic in tests.
    /// Production callers should use the parameterless constructor, whose socket callback repeats
    /// loopback validation at connection time to avoid a DNS rebinding window.
    /// </remarks>
    internal OpenAiCompatibleEmbeddingGenerator(
        HttpMessageHandler handler,
        Func<string, IReadOnlyList<IPAddress>> resolveHost,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _resolveHost = resolveHost ?? throw new ArgumentNullException(nameof(resolveHost));
        _readEnvironment = readEnvironment ?? throw new ArgumentNullException(nameof(readEnvironment));
        _client = new HttpClient(handler, disposeHandler: true)
        {
            // HttpClient's default 100s timeout would silently clamp config.TimeoutSeconds.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public string GetProviderFingerprint(DocsAnalysisEmbeddingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var endpoint = RequireEndpoint(config);
        var identity = $"openai-compatible/v1\n{endpoint.AbsoluteUri}";
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

        var orderedKeys = keys.ToArray();
        var orderedInputs = inputs.ToArray();
        if (orderedKeys.Length != orderedInputs.Length)
            throw new ArgumentException("Embedding keys and inputs must have the same count.", nameof(inputs));
        if (orderedKeys.Length == 0)
            return new EmbeddingGenerationResult([], EmbeddingUsage.None);
        if (config.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "Embedding batch size must be positive.");
        if (config.TimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "Embedding timeout must be positive.");

        var endpoint = RequireEndpoint(config);
        EnsureLoopback(endpoint);
        var model = string.IsNullOrWhiteSpace(config.Model)
            ? throw new ArgumentException("An embedding model is required.", nameof(config))
            : config.Model;
        var bearerToken = string.IsNullOrWhiteSpace(config.ApiKeyEnv)
            ? null
            : _readEnvironment(config.ApiKeyEnv);

        var result = new List<StoredEmbedding>(orderedKeys.Length);
        var usage = EmbeddingUsage.None;
        for (var offset = 0; offset < orderedInputs.Length; offset += config.BatchSize)
        {
            var count = Math.Min(config.BatchSize, orderedInputs.Length - offset);
            var batchInputs = orderedInputs.AsSpan(offset, count).ToArray();
            var parsed = SendBatchAsync(
                    endpoint,
                    model,
                    config.Dimensions,
                    batchInputs,
                    bearerToken,
                    config.TimeoutSeconds)
                .GetAwaiter()
                .GetResult();
            for (var index = 0; index < parsed.Vectors.Count; index++)
                result.Add(new StoredEmbedding(orderedKeys[offset + index], parsed.Vectors[index]));
            usage = usage.Add(parsed.Usage);
        }

        return new EmbeddingGenerationResult(result, usage);
    }

    private async Task<ParsedBatch> SendBatchAsync(
        Uri endpoint,
        string model,
        int? dimensions,
        IReadOnlyList<string> inputs,
        string? bearerToken,
        int timeoutSeconds)
    {
        using var request = CreateRequest(endpoint, model, dimensions, inputs, bearerToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await _client.SendAsync(
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

        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token)
            .ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(
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
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["input"] = inputs,
            ["model"] = model,
            ["encoding_format"] = "float"
        };
        if (dimensions is not null) payload["dimensions"] = dimensions.Value;

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
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
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The embedding response must contain a data array with complete indices.");

        var vectors = new IReadOnlyList<float>?[expectedCount];
        var dimensions = configuredDimensions;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var indexValue)
                || !indexValue.TryGetInt32(out var index)
                || index < 0
                || index >= expectedCount
                || vectors[index] is not null)
            {
                throw new InvalidDataException("Embedding response index values must be unique and in range.");
            }

            if (!item.TryGetProperty("embedding", out var embedding)
                || embedding.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Each embedding response item must contain a finite vector.");
            }

            var raw = embedding.EnumerateArray().Select(ReadFiniteFloat).ToArray();
            dimensions ??= raw.Length;
            if (raw.Length == 0 || raw.Length != dimensions.Value)
                throw new InvalidDataException("Embedding vector dimensions must be non-zero and consistent.");
            vectors[index] = Normalize(raw);
        }

        if (vectors.Any(vector => vector is null))
            throw new InvalidDataException("Embedding response index values must be complete.");

        return new ParsedBatch(
            vectors.Select(vector => vector!).ToArray(),
            ReadUsage(root));
    }

    private static float ReadFiniteFloat(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number > float.MaxValue || number < -float.MaxValue)
        {
            throw new InvalidDataException("Embedding vectors must contain only finite values.");
        }

        return (float)number;
    }

    private static IReadOnlyList<float> Normalize(IReadOnlyList<float> vector)
    {
        var squaredNorm = vector.Sum(value => (double)value * value);
        if (!double.IsFinite(squaredNorm) || squaredNorm <= 0)
            throw new InvalidDataException("Embedding vectors must have a finite, non-zero norm.");

        var norm = Math.Sqrt(squaredNorm);
        return vector.Select(value => (float)(value / norm)).ToArray();
    }

    private static EmbeddingUsage ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return EmbeddingUsage.None;

        return new EmbeddingUsage(
            ReadNonNegativeInt(usage, "prompt_tokens"),
            ReadNonNegativeInt(usage, "total_tokens"));
    }

    private static int ReadNonNegativeInt(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value)) return 0;
        if (!value.TryGetInt32(out var count) || count < 0)
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
        var endpoint = config.Endpoint
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

    private static IReadOnlyList<IPAddress> ResolveHost(string host) => Dns.GetHostAddresses(host);

    private static SocketsHttpHandler CreateLocalOnlyHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = ConnectLoopback
    };

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "A successful NetworkStream takes socket ownership; every failed socket is disposed in the catch block.")]
    private static async ValueTask<Stream> ConnectLoopback(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(address => !LoopbackAddress.IsLoopback(address)))
            throw new HttpRequestException("The embedding endpoint must resolve only to loopback addresses.");

        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastFailure = ex;
            }
        }

        throw new HttpRequestException("No loopback address accepted the embedding connection.", lastFailure);
    }

    private sealed record ParsedBatch(
        IReadOnlyList<IReadOnlyList<float>> Vectors,
        EmbeddingUsage Usage);
}
