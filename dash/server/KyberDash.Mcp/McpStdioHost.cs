using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;

namespace KyberDash.Mcp;

/// <summary>
/// Read-only KyberDash analytics MCP server speaking newline-delimited JSON-RPC 2.0 over stdio.
/// </summary>
/// <remarks>
/// A tool call reaches the same <see cref="IAnalyticsService"/> through one of two transports. When
/// a live dashboard answers on loopback, the call is forwarded over HTTP so it observes the engine's
/// already-warm projections; otherwise the container's own analytics service executes it directly,
/// so an agent works with no dashboard running at all. Both paths produce the identical
/// contract-v1 envelope.
/// </remarks>
public partial class McpStdioHost
{
    /// <summary>Largest analytics envelope this server will hand back to an agent.</summary>
    public const int MaxReplyBytes = 65_536;

    /// <summary>Longest engine reply excerpt copied into a diagnostic message.</summary>
    private const int MaxSnippetChars = 200;

    /// <summary>
    /// How long a single forwarded analytics request may take. A store holding a few hundred thousand
    /// records answers one analytics operation in roughly ten seconds, because the operation projects
    /// and lists sessions; a shorter bound would report a timeout for an answer the engine is still
    /// producing.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IAnalyticsService _analytics;
    private readonly IEngineLifecycleService _lifecycle;
    private readonly HttpClient _httpClient;
    private readonly Uri? _explicitServerUri;
    private readonly string? _stateDir;
    private readonly bool _verbose;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a stdio host over an already-resolved service container.</summary>
    /// <param name="analytics">The analytics service used when no live dashboard is reachable.</param>
    /// <param name="lifecycle">Resolves the live dashboard, if one is running.</param>
    /// <param name="httpClient">The loopback HTTP client used to forward tool calls.</param>
    /// <param name="explicitServerUri">A <c>--server-url</c> override, already validated.</param>
    /// <param name="stateDir">The <c>--state-dir</c> override.</param>
    /// <param name="verbose">Whether to write diagnostics to stderr.</param>
    /// <param name="timeProvider">Clock supplying the default scope window; defaults to <see cref="TimeProvider.System"/>.</param>
    public McpStdioHost(
        IAnalyticsService analytics,
        IEngineLifecycleService lifecycle,
        HttpClient httpClient,
        Uri? explicitServerUri = null,
        string? stateDir = null,
        bool verbose = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(httpClient);

        _analytics = analytics;
        _lifecycle = lifecycle;
        _httpClient = httpClient;
        _explicitServerUri = explicitServerUri;
        _stateDir = stateDir;
        _verbose = verbose;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates a stdio host from the raw <c>--server-url</c> text.</summary>
    /// <param name="analytics">The analytics service used when no live dashboard is reachable.</param>
    /// <param name="lifecycle">Resolves the live dashboard, if one is running.</param>
    /// <param name="httpClient">The loopback HTTP client used to forward tool calls.</param>
    /// <param name="explicitServerUrl">The <c>--server-url</c> text, validated here.</param>
    /// <param name="stateDir">The <c>--state-dir</c> override.</param>
    /// <param name="verbose">Whether to write diagnostics to stderr.</param>
    /// <param name="timeProvider">Clock supplying the default scope window; defaults to <see cref="TimeProvider.System"/>.</param>
    public McpStdioHost(
        IAnalyticsService analytics,
        IEngineLifecycleService lifecycle,
        HttpClient httpClient,
        string? explicitServerUrl,
        string? stateDir = null,
        bool verbose = false,
        TimeProvider? timeProvider = null)
        : this(
            analytics,
            lifecycle,
            httpClient,
            explicitServerUrl is not null && TryValidateLoopbackServerUrl(explicitServerUrl, out Uri? parsed) ? parsed : null,
            stateDir,
            verbose,
            timeProvider)
    {
    }

    [GeneratedRegex(@"^(engine_unavailable|incompatible_engine|budget_exceeded|invalid_request|cancelled|internal_error):", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StablePrefixRegex();

    /// <summary>
    /// Serves JSON-RPC requests from <paramref name="input"/> onto <paramref name="output"/> until input ends.
    /// </summary>
    /// <param name="input">The stdio reader the MCP client writes to.</param>
    /// <param name="output">The stdio writer the MCP client reads from.</param>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>The process exit code.</returns>
    public async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? response = await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
            if (response is null)
                continue;

            await output.WriteLineAsync(response.ToJsonString(CompactJsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    /// <summary>Handles one JSON-RPC line, returning the response to write or <see langword="null"/> for a notification.</summary>
    /// <param name="line">One JSON-RPC request object.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    /// <returns>The response object, or <see langword="null"/> when no reply is due.</returns>
    public async Task<JsonNode?> HandleLineAsync(string line, CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return ErrorResponse(null, -32700, "Parse error");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ErrorResponse(null, -32600, "Invalid Request");

            bool hasId = root.TryGetProperty("id", out JsonElement idElement)
                && idElement.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null;
            JsonNode? idNode = hasId ? JsonNode.Parse(idElement.GetRawText()) : null;

            string? method = root.TryGetProperty("method", out JsonElement methodElement) && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;
            if (string.IsNullOrEmpty(method))
                return hasId ? ErrorResponse(idNode, -32600, "Invalid Request: missing method") : null;

            // Notifications (initialize handshake completion, and anything else without an id) expect no reply.
            if (!hasId)
                return null;

            root.TryGetProperty("params", out JsonElement paramsElement);
            return method switch
            {
                "initialize" => SuccessResponse(idNode, BuildInitializeResult(paramsElement)),
                "ping" => SuccessResponse(idNode, new JsonObject()),
                "tools/list" => SuccessResponse(idNode, BuildToolsListResult()),
                "tools/call" => await HandleToolCallAsync(idNode, paramsElement, cancellationToken).ConfigureAwait(false),
                "resources/list" => SuccessResponse(idNode, new JsonObject { ["resources"] = new JsonArray() }),
                "prompts/list" => SuccessResponse(idNode, new JsonObject { ["prompts"] = new JsonArray() }),
                _ => ErrorResponse(idNode, -32601, $"Method not found: {method}"),
            };
        }
    }

    private static JsonObject BuildInitializeResult(JsonElement paramsElement)
    {
        const string DefaultProtocolVersion = "2025-06-18";
        string protocolVersion = DefaultProtocolVersion;
        if (paramsElement.ValueKind == JsonValueKind.Object
            && paramsElement.TryGetProperty("protocolVersion", out JsonElement versionElement)
            && versionElement.ValueKind == JsonValueKind.String
            && versionElement.GetString() is { } requestedVersion
            && !string.IsNullOrWhiteSpace(requestedVersion))
        {
            protocolVersion = requestedVersion;
        }

        return new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
                ["resources"] = new JsonObject(),
                ["prompts"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "kyberdash-analytics",
                ["version"] = "1.0.0",
            },
        };
    }

    private static JsonObject BuildToolsListResult()
    {
        JsonArray list = [];
        foreach (DashToolCatalog.DashTool tool in DashToolCatalog.Tools)
        {
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.SchemaJson),
            });
        }

        return new JsonObject { ["tools"] = list };
    }

    private async Task<JsonNode> HandleToolCallAsync(JsonNode? idNode, JsonElement paramsElement, CancellationToken cancellationToken)
    {
        if (paramsElement.ValueKind != JsonValueKind.Object
            || !paramsElement.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            return ErrorResponse(idNode, -32602, "Invalid params: missing tool name");
        }

        string toolName = nameElement.GetString() ?? string.Empty;
        DashToolCatalog.DashTool? tool = DashToolCatalog.Find(toolName);
        if (tool is null)
            return ErrorResponse(idNode, -32602, $"invalid_request: unknown analytics tool {toolName}.");

        JsonElement arguments = paramsElement.TryGetProperty("arguments", out JsonElement rawArguments)
            && rawArguments.ValueKind == JsonValueKind.Object
                ? rawArguments
                : default;

        try
        {
            return await ExecuteToolAsync(idNode, tool, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ErrorResponse(idNode, -32000, "engine_unavailable: the analytics request timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return ErrorResponse(idNode, -32000, $"engine_unavailable: the analytics request failed ({ex.Message}).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or DashException)
        {
            string message = StablePrefixRegex().IsMatch(ex.Message)
                ? ex.Message
                : $"engine_unavailable: the analytics request failed ({ex.Message}).";
            return ErrorResponse(idNode, -32000, message);
        }
    }

    private async Task<JsonNode> ExecuteToolAsync(
        JsonNode? idNode,
        DashToolCatalog.DashTool tool,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        Uri? serverUri = await ResolveServerUriAsync(cancellationToken).ConfigureAwait(false);
        JsonObject? envelope = serverUri is not null
            ? await ForwardOverHttpAsync(serverUri, tool, arguments, cancellationToken).ConfigureAwait(false)
            : null;

        if (envelope is null)
            envelope = ExecuteDirectly(tool, arguments);

        if (envelope["error"] is not null)
            return SuccessResponse(idNode, BuildToolTextResult(envelope, isError: true));

        string serialized = envelope.ToJsonString(CompactJsonOptions);
        if (Encoding.UTF8.GetByteCount(serialized) > MaxReplyBytes)
        {
            JsonObject bounded = BuildErrorEnvelope(
                "budget_exceeded",
                "The analytics result exceeded the 64 KiB MCP response limit.",
                "Narrow the request or use its cursor.");
            return SuccessResponse(idNode, BuildToolTextResult(bounded, isError: true));
        }

        return SuccessResponse(idNode, BuildToolTextResult(envelope, isError: false));
    }

    private async Task<Uri?> ResolveServerUriAsync(CancellationToken cancellationToken)
    {
        if (_explicitServerUri is not null)
            return _explicitServerUri;

        try
        {
            return await _lifecycle.ResolveLiveServerUriAsync(_stateDir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or DashException)
        {
            if (_verbose)
                await Console.Error.WriteLineAsync($"kyberdash-mcp: live engine lookup failed: {ex.Message}").ConfigureAwait(false);

            return null;
        }
    }

    private async Task<JsonObject?> ForwardOverHttpAsync(
        Uri serverUri,
        DashToolCatalog.DashTool tool,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string requestUrl = $"{serverUri.GetLeftPart(UriPartial.Authority)}/api/kyber/analytics/{tool.Operation}";
        using HttpRequestMessage request = new(
            string.Equals(tool.Method, "POST", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get,
            requestUrl);

        if (string.Equals(tool.Method, "POST", StringComparison.Ordinal))
        {
            JsonObject engineBody = BuildEngineBody(tool.Name, arguments);
            request.Content = new StringContent(engineBody.ToJsonString(CompactJsonOptions), Encoding.UTF8, "application/json");
        }

        {
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            byte[] rawBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (rawBytes.Length > MaxReplyBytes)
                throw new InvalidOperationException("budget_exceeded: engine response exceeded 64 KiB.");

            string rawText;
            try
            {
                rawText = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(rawBytes);
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"incompatible_engine: the analytics engine answered HTTP {(int)response.StatusCode} with a body that is not valid UTF-8."));
            }

            // A non-success status is reported as itself. Parsing first would turn an empty or HTML
            // engine answer into "malformed JSON", which names the symptom and hides the status.
            if (!response.IsSuccessStatusCode)
            {
                JsonObject? errorEnvelope = TryParseContractEnvelope(rawText);
                if (errorEnvelope?["error"] is JsonObject)
                    return errorEnvelope;

                if (errorEnvelope is not null)
                {
                    throw new InvalidOperationException(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"engine_unavailable: the analytics engine answered HTTP {(int)response.StatusCode} with a contract version 1 envelope that carries no error."));
                }

                throw new InvalidOperationException(DescribeFailedReply(response.StatusCode, rawText));
            }

            JsonNode? parsed = TryParse(rawText, response.StatusCode);
            if (parsed is not JsonObject envelope || !IsValidContractV1Envelope(envelope))
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"incompatible_engine: the analytics engine answered HTTP {(int)response.StatusCode} with a body that is not an analytics contract version 1 envelope."));
            }

            return envelope;
        }
    }

    /// <summary>Names the real cause of a non-success engine reply so an agent can act on it.</summary>
    /// <param name="statusCode">The HTTP status the engine answered with.</param>
    /// <param name="rawText">The reply body, possibly empty or not JSON.</param>
    /// <returns>A message carrying the status code and the shape of the body.</returns>
    private static string DescribeFailedReply(HttpStatusCode statusCode, string rawText)
    {
        string code = ((int)statusCode).ToString(CultureInfo.InvariantCulture);
        string body = rawText.Trim();
        if (body.Length == 0)
        {
            return $"engine_unavailable: the analytics engine answered HTTP {code} with an empty body. "
                + "Check the engine log for the failure behind it.";
        }

        if (body[0] is not ('{' or '['))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"engine_unavailable: the analytics engine answered HTTP {code} with a non-JSON body ({Snippet(body)}).");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"engine_unavailable: the analytics engine answered HTTP {code} with a body that is not an analytics contract version 1 envelope ({Snippet(body)}).");
    }

    /// <summary>Bounds a reply body for a message an agent reads.</summary>
    /// <param name="body">The reply body text.</param>
    /// <returns>At most <see cref="MaxSnippetChars"/> characters, whitespace collapsed.</returns>
    private static string Snippet(string body)
    {
        string collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= MaxSnippetChars)
        {
            return collapsed;
        }

        // A cut that lands after a high surrogate would emit half a character; drop it so the
        // diagnostic stays valid text.
        int length = MaxSnippetChars;
        if (char.IsHighSurrogate(collapsed[length - 1]))
        {
            length--;
        }

        return string.Concat(collapsed.AsSpan(0, length), "…");
    }

    /// <summary>Parses a reply that is expected to be a contract envelope, or reports why it is not.</summary>
    /// <param name="rawText">The reply body text.</param>
    /// <param name="statusCode">The HTTP status the engine answered with.</param>
    /// <returns>The parsed node.</returns>
    private static JsonNode? TryParse(string rawText, HttpStatusCode statusCode)
    {
        string body = rawText.Trim();
        if (body.Length == 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"incompatible_engine: the analytics engine answered HTTP {(int)statusCode} with an empty body."));
        }

        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"incompatible_engine: the analytics engine answered HTTP {(int)statusCode} with malformed JSON ({ex.Message})."),
                ex);
        }
    }

    /// <summary>Parses a reply as a contract envelope, returning <see langword="null"/> when it is not one.</summary>
    /// <param name="rawText">The reply body text.</param>
    /// <returns>The envelope, or <see langword="null"/>.</returns>
    private static JsonObject? TryParseContractEnvelope(string rawText)
    {
        try
        {
            return JsonNode.Parse(rawText.Trim()) is JsonObject envelope && IsValidContractV1Envelope(envelope)
                ? envelope
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private JsonObject ExecuteDirectly(DashToolCatalog.DashTool tool, JsonElement arguments)
    {
        JsonElement? request = string.Equals(tool.Method, "POST", StringComparison.Ordinal)
            ? JsonSerializer.SerializeToElement(BuildEngineBody(tool.Name, arguments), CompactJsonOptions)
            : null;

        AnalyticsExecutionResult result = _analytics.Execute(tool.Operation, request);
        JsonNode? node = JsonNode.Parse(result.Envelope.GetRawText());
        if (node is not JsonObject envelope || !IsValidContractV1Envelope(envelope))
        {
            throw new InvalidOperationException("incompatible_engine: the analytics service did not return a contract version 1 envelope.");
        }

        return envelope;
    }

    /// <summary>Validates a candidate <c>--server-url</c> that has already been parsed as a URI.</summary>
    /// <param name="url">The candidate URI.</param>
    /// <returns>Whether the URI is a loopback HTTP URL with no path, query, or user info.</returns>
    public static bool IsAllowedLoopbackServerUrl(Uri? url)
    {
        if (url is null)
            return false;

        if (!string.Equals(url.Scheme, "http", StringComparison.Ordinal)
            || !string.IsNullOrEmpty(url.UserInfo)
            || url.Port <= 0
            || url.Port > 65535
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment)
            || (!string.Equals(url.AbsolutePath, "/", StringComparison.Ordinal) && !string.IsNullOrEmpty(url.AbsolutePath)))
        {
            return false;
        }

        return string.Equals(url.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(url.Host, "[::1]", StringComparison.Ordinal)
            || string.Equals(url.Host, "::1", StringComparison.Ordinal);
    }

    /// <summary>Validates and normalises a literal loopback <c>--server-url</c>.</summary>
    /// <param name="rawUrl">The candidate URL.</param>
    /// <param name="normalized">The canonical <c>http://127.0.0.1:&lt;port&gt;</c> form.</param>
    /// <returns>Whether the URL is a loopback HTTP URL with no path, query, or user info.</returns>
    internal static bool TryValidateLoopbackServerUrl(string rawUrl, out Uri? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out Uri? uri)
            || !IsAllowedLoopbackServerUrl(uri))
        {
            return false;
        }

        string hostPart = uri.HostNameType == UriHostNameType.IPv6
            ? string.Create(CultureInfo.InvariantCulture, $"[{IPAddress.IPv6Loopback}]")
            : "127.0.0.1";
        normalized = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://{hostPart}:{uri.Port}/"));
        return true;
    }

    /// <summary>
    /// Reshapes the agent-facing arguments into the engine's request body. The tool schemas are
    /// written for agents (names like <c>text</c> and <c>groups</c>); the engine accepts its own
    /// field names.
    /// </summary>
    private JsonObject BuildEngineBody(string toolName, JsonElement arguments)
    {
        JsonObject body = arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(arguments.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();

        if (string.Equals(toolName, "dash_search_sessions_by_text_and_scope", StringComparison.Ordinal))
        {
            ApplySearchSessionsBody(body);
            return body;
        }

        if (string.Equals(toolName, "dash_read_captured_run_session_or_turn_context", StringComparison.Ordinal))
        {
            ApplyReadCapturedContextBody(body);
            return body;
        }

        if (string.Equals(toolName, "dash_query_session_cost_tokens_and_duration", StringComparison.Ordinal))
        {
            ApplySessionCostBody(body);
            return body;
        }

        if (string.Equals(toolName, "dash_list_detected_context_and_tool_problems", StringComparison.Ordinal)
            || string.Equals(toolName, "dash_get_run_success_or_failure_outcomes", StringComparison.Ordinal))
        {
            ApplyWindowDays(body);
            return body;
        }

        return body;
    }

    private void ApplySearchSessionsBody(JsonObject body)
    {
        if (!body.ContainsKey("query") && body.TryGetPropertyValue("text", out JsonNode? textNode) && textNode is not null)
        {
            body.Remove("text");
            body["query"] = textNode.DeepClone();
        }

        ApplyWindowDays(body);
    }

    private static void ApplyReadCapturedContextBody(JsonObject body)
    {
        if (body.ContainsKey("mode") || body.ContainsKey("target") || body.ContainsKey("kind"))
            return;

        if (body.TryGetPropertyValue("sessionId", out JsonNode? sessionId) && sessionId is not null)
        {
            body.Remove("sessionId");
            body["kind"] = "session";
            body["id"] = sessionId.DeepClone();
        }
    }

    private void ApplySessionCostBody(JsonObject body)
    {
        MapMetricIds(body);
        if (body.TryGetPropertyValue("groups", out JsonNode? groupsNode) && groupsNode is not null)
        {
            body.Remove("groups");
            body["groupBy"] = groupsNode.DeepClone();
        }

        ApplyWindowDays(body);
        if (!body.ContainsKey("catalogVersion"))
            body["catalogVersion"] = 1;
    }

    private static void MapMetricIds(JsonObject body)
    {
        if (!body.TryGetPropertyValue("metrics", out JsonNode? metricsNode) || metricsNode is not JsonArray metrics)
            return;

        foreach (JsonNode? metric in metrics)
        {
            if (metric?.GetValueKind() != JsonValueKind.String)
                return;
        }

        JsonArray mapped = [];
        foreach (JsonNode? metric in metrics)
            mapped.Add(new JsonObject { ["id"] = metric?.GetValue<string>() ?? string.Empty });

        body["metrics"] = mapped;
    }

    private void ApplyWindowDays(JsonObject body)
    {
        if (!body.TryGetPropertyValue("windowDays", out JsonNode? node))
            return;

        body.Remove("windowDays");
        if (node is null)
            return;

        int days;
        try
        {
            days = node.GetValue<int>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new InvalidOperationException("invalid_request: windowDays must be an integer between 1 and 365.", ex);
        }

        if (days is < 1 or > 365)
            throw new InvalidOperationException("invalid_request: windowDays must be an integer between 1 and 365.");

        DateTimeOffset end = _timeProvider.GetUtcNow();
        DateTimeOffset start = end.AddDays(-days);
        JsonObject scope = body["scope"] as JsonObject ?? new JsonObject();
        if (!scope.ContainsKey("from"))
            scope["from"] = start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        if (!scope.ContainsKey("to"))
            scope["to"] = end.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        body["scope"] = scope;
    }

    private static bool IsValidContractV1Envelope(JsonObject envelope)
    {
        return envelope.TryGetPropertyValue("contractVersion", out JsonNode? version)
            && version is JsonValue value
            && value.TryGetValue(out int v)
            && v == 1
            && envelope.ContainsKey("data")
            && envelope["scope"] is JsonObject
            && envelope["measurement"] is JsonObject
            && envelope["coverage"] is JsonObject
            && envelope["freshness"] is JsonObject
            && envelope["evidence"] is JsonArray
            && envelope["pagination"] is JsonObject
            && (envelope["error"] is null or JsonObject);
    }

    private static JsonObject BuildErrorEnvelope(string code, string message, string? hint = null)
    {
        JsonObject error = new()
        {
            ["code"] = code,
            ["message"] = message,
        };
        if (hint is not null)
            error["hint"] = hint;

        return new JsonObject
        {
            ["contractVersion"] = 1,
            ["scope"] = new JsonObject(),
            ["data"] = null,
            ["measurement"] = new JsonObject(),
            ["coverage"] = new JsonObject(),
            ["freshness"] = new JsonObject(),
            ["evidence"] = new JsonArray(),
            ["pagination"] = new JsonObject(),
            ["error"] = error,
        };
    }

    private static JsonObject BuildToolTextResult(JsonObject envelope, bool isError)
    {
        JsonObject result = new()
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = envelope.ToJsonString(CompactJsonOptions),
                },
            },
            ["structuredContent"] = envelope.DeepClone(),
        };
        if (isError)
            result["isError"] = true;

        return result;
    }

    private static JsonObject SuccessResponse(JsonNode? id, JsonNode result)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };
    }

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message)
    {
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
    }
}
