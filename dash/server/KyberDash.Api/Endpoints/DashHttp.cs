using System.Globalization;
using System.Net;
using System.Text.Json;
using KyberDash.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KyberDash.Api.Endpoints;

/// <summary>
/// Query-string parsing, loopback mutation guards, and response helpers shared by the REST and
/// OTLP endpoint groups.
/// </summary>
/// <remarks>
/// These helpers reproduce the wire behaviour of the pre-rewrite <c>KyberApiServer</c> and
/// <c>OtlpHttpServer</c> exactly, including the error envelopes, because <c>dash/web</c>, the tray,
/// and the MCP proxy already parse those shapes.
/// </remarks>
internal static class DashHttp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns <see langword="true"/> when the request may mutate state: it arrived on loopback and
    /// carries no <c>Origin</c> header, so it cannot be a browser page making a cross-site write.
    /// </summary>
    public static bool IsAllowedMutation(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.ContainsKey("Origin"))
            return false;

        IPAddress? remote = request.HttpContext.Connection.RemoteIpAddress;
        return remote is null || IPAddress.IsLoopback(remote);
    }

    /// <summary>Writes the standard <c>{"error":"…"}</c> body with <paramref name="statusCode"/>.</summary>
    public static IResult Error(int statusCode, string message)
    {
        return Results.Json(new { error = message }, JsonOptions, statusCode: statusCode);
    }

    /// <summary>Writes the standard JSON body, suppressing caches.</summary>
    public static IResult NoStoreJson(int statusCode, object payload)
    {
        return WithHeaders(
            Results.Json(payload, JsonOptions, contentType: "application/json; charset=utf-8", statusCode: statusCode),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Cache-Control"] = "no-store" });
    }

    /// <summary>Writes a JSON body and applies extra response headers.</summary>
    public static IResult JsonWithHeaders(int statusCode, object payload, IReadOnlyDictionary<string, string>? headers)
    {
        IResult inner = Results.Json(payload, JsonOptions, contentType: "application/json; charset=utf-8", statusCode: statusCode);
        return headers is null || headers.Count == 0 ? inner : WithHeaders(inner, headers);
    }

    /// <summary>Wraps <paramref name="inner"/> so the given headers are set before it executes.</summary>
    public static IResult WithHeaders(IResult inner, IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new HeaderDecoratingResult(inner, headers);
    }

    /// <summary>Serialises a <see cref="JsonElement"/> result, or its 404 when the service found nothing.</summary>
    public static IResult FromNullableJson(JsonElement? payload, string notFoundMessage)
    {
        return payload.HasValue && payload.Value.ValueKind != JsonValueKind.Undefined
            ? Results.Json(payload.Value, JsonOptions)
            : Error(StatusCodes.Status404NotFound, notFoundMessage);
    }

    /// <summary>Reads a whole number query value when it is positive.</summary>
    public static int? OptionalPositiveInt(IQueryCollection query, string key) =>
        int.TryParse(query[key].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : null;

    /// <summary>Reads a query value, treating a missing or blank entry as absent.</summary>
    public static string? OptionalQueryString(IQueryCollection query, string key)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues values))
            return null;

        string raw = values.ToString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>Reads a whole number query value when it is zero or greater.</summary>
    public static int? OptionalNonNegativeInt(IQueryCollection query, string key) =>
        int.TryParse(query[key].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0
            ? value
            : null;

    /// <summary>Resolves <c>limit</c>/<c>page</c>/<c>offset</c> into a clamped page window.</summary>
    public static (int Limit, int Offset, int Page) Pagination(IQueryCollection query, int defaultLimit)
    {
        int limit = Math.Clamp(OptionalPositiveInt(query, "limit") ?? defaultLimit, 1, 500);
        int? rawPage = OptionalPositiveInt(query, "page");
        int offset = rawPage.HasValue
            ? (rawPage.Value - 1) * limit
            : OptionalNonNegativeInt(query, "offset") ?? 0;
        return (limit, offset, (offset / limit) + 1);
    }

    /// <summary>Reads a boolean query value written as <c>true</c> or <c>1</c>.</summary>
    public static bool IsTruthy(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "1", StringComparison.Ordinal);

    /// <summary>Trims an optional filter, treating blank and <c>all</c> as "no filter".</summary>
    public static string? NormalizeFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            return null;

        return raw.Trim();
    }

    /// <summary>
    /// Reads a JSON request body. Returns <see langword="false"/> with a 400 response when the body
    /// is empty or malformed, matching the engine's existing response text.
    /// </summary>
    public static async Task<(bool Ok, JsonElement Body, IResult? Failure)> ReadJsonBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[] bytes;
        using (MemoryStream buffer = new())
        {
            await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        if (bytes.Length == 0)
            return (false, default, Error(StatusCodes.Status400BadRequest, "Request body is empty"));

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            return (true, document.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return (false, default, Error(StatusCodes.Status400BadRequest, $"Malformed JSON body: {ex.Message}"));
        }
    }

    /// <summary>Maps an OTLP admission decision onto its HTTP response.</summary>
    public static IResult OtlpAdmission(IngestAdmissionResult admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.Accepted)
            return Results.Json(new { ok = true }, JsonOptions);

        bool oversize = admission.HttpStatusCode == StatusCodes.Status413PayloadTooLarge;
        Dictionary<string, string>? headers = null;
        if (admission.RetryAfterSeconds.HasValue)
        {
            headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Retry-After"] = admission.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture),
            };
        }

        int statusCode = admission.HttpStatusCode is StatusCodes.Status413PayloadTooLarge or StatusCodes.Status503ServiceUnavailable
            ? admission.HttpStatusCode
            : StatusCodes.Status503ServiceUnavailable;

        return JsonWithHeaders(
            statusCode,
            new
            {
                error = new
                {
                    code = oversize ? "OTLP_PAYLOAD_TOO_LARGE" : "OTLP_BACKPRESSURE",
                    message = admission.ErrorMessage ?? "Ingestion buffer full; retry",
                },
            },
            headers);
    }

    /// <summary>Describes a content type for a static asset path, or <see langword="null"/> when unknown.</summary>
    public static string? ContentTypeForPath(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
            return null;

        return extension.ToUpperInvariant() switch
        {
            ".HTML" => "text/html; charset=utf-8",
            ".JS" => "text/javascript; charset=utf-8",
            ".CSS" => "text/css; charset=utf-8",
            ".SVG" => "image/svg+xml",
            ".JSON" => "application/json; charset=utf-8",
            ".WOFF2" => "font/woff2",
            ".WOFF" => "font/woff",
            ".PNG" => "image/png",
            ".ICO" => "image/x-icon",
            ".MAP" => "application/json",
            _ => null,
        };
    }

    /// <summary>Sets response headers, then defers to the result it wraps.</summary>
    private class HeaderDecoratingResult(IResult inner, IReadOnlyDictionary<string, string> headers) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            foreach (KeyValuePair<string, string> header in headers)
                httpContext.Response.Headers[header.Key] = header.Value;

            return inner.ExecuteAsync(httpContext);
        }
    }
}
