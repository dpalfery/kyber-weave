using System.Globalization;
using System.Net;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
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
    /// <summary>Maximum JSON request body accepted by the mutation endpoints (1 MiB).</summary>
    public const int DefaultMaxJsonBodyBytes = 1024 * 1024;

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
        OptionalPositiveInt(query, key, out _);

    /// <summary>
    /// Reads a whole number query value when it is positive. <paramref name="invalid"/> reports that
    /// the value was present but not a positive whole number, so a caller can answer 400 instead of
    /// treating it as absent and reading without a bound.
    /// </summary>
    public static int? OptionalPositiveInt(IQueryCollection query, string key, out bool invalid)
    {
        invalid = false;
        if (!query.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues values))
            return null;

        string raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Present but blank is a malformed value, not an absent one: treating it as absent
            // would run the query without the bound the caller asked for.
            invalid = true;
            return null;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0)
            return value;

        invalid = true;
        return null;
    }

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
        OptionalNonNegativeInt(query, key, out _);

    /// <summary>
    /// Reads a whole number query value when it is zero or greater. <paramref name="invalid"/>
    /// reports that the value was present but not a non-negative whole number.
    /// </summary>
    public static int? OptionalNonNegativeInt(IQueryCollection query, string key, out bool invalid)
    {
        invalid = false;
        if (!query.TryGetValue(key, out Microsoft.Extensions.Primitives.StringValues values))
            return null;

        string raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            invalid = true;
            return null;
        }

        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0)
            return value;

        invalid = true;
        return null;
    }

    /// <summary>
    /// Resolves <c>limit</c>/<c>page</c>/<c>offset</c> into a clamped page window.
    /// <paramref name="invalid"/> reports a value that was present but malformed, so a caller can
    /// answer 400 instead of paging with a silent default.
    /// </summary>
    /// <param name="query">The request's query string.</param>
    /// <param name="defaultLimit">Rows per page when the request names no limit.</param>
    /// <param name="invalid">Set when a paging value is present but malformed.</param>
    /// <param name="maxLimit">The largest page the route serves; a larger <c>limit</c> is clamped to it.</param>
    /// <returns>The clamped limit, the row offset, and the one-based page.</returns>
    public static (int Limit, int Offset, int Page) Pagination(IQueryCollection query, int defaultLimit, out bool invalid, int maxLimit = 500)
    {
        int? requestedLimit = OptionalPositiveInt(query, "limit", out bool invalidLimit);
        int? rawPage = OptionalPositiveInt(query, "page", out bool invalidPage);
        int? rawOffset = OptionalNonNegativeInt(query, "offset", out bool invalidOffset);

        invalid = invalidLimit || invalidPage || invalidOffset;
        if (invalid)
            return (defaultLimit, 0, 1);

        int limit = Math.Clamp(requestedLimit ?? defaultLimit, 1, maxLimit);
        // A page number is only bounded by int, so page × limit overflows int for a huge page.
        // Compute in long and clamp: an absurd page then reads as "past the end" instead of
        // wrapping to a small offset and returning a different page's rows.
        int offset = rawPage.HasValue
            ? (int)Math.Clamp((long)(rawPage.Value - 1) * limit, 0, int.MaxValue)
            : rawOffset ?? 0;
        long page = ((long)offset / limit) + 1;
        return (limit, offset, (int)Math.Min(page, int.MaxValue));
    }

    /// <summary>
    /// Reads a section page <c>limit</c> query parameter, accepting only <c>25</c>, <c>50</c>, or
    /// <c>100</c> (or <paramref name="defaultLimit"/> when absent), or sets <paramref name="refusal"/>
    /// to an HTTP 400 <see cref="Error"/> response.
    /// </summary>
    public static bool TrySectionLimit(IQueryCollection query, int defaultLimit, out int limit, out IResult? refusal)
    {
        ArgumentNullException.ThrowIfNull(query);
        int? requested = OptionalPositiveInt(query, "limit", out bool invalid);
        if (invalid)
        {
            limit = defaultLimit;
            refusal = Error(
                StatusCodes.Status400BadRequest,
                "limit and page must be positive whole numbers and offset a non-negative whole number");
            return false;
        }

        if (requested is int value && value is not (25 or 50 or 100))
        {
            limit = defaultLimit;
            refusal = Error(StatusCodes.Status400BadRequest, "limit must be 25, 50, or 100");
            return false;
        }

        limit = requested ?? defaultLimit;
        refusal = null;
        return true;
    }

    /// <summary>
    /// Reads a section <c>order</c> query parameter (<c>"desc"</c> by default, or trimmed
    /// case-insensitive <c>"desc"</c> / <c>"asc"</c>), or sets <paramref name="refusal"/> to an
    /// HTTP 400 <see cref="Error"/> response on blank or unknown values.
    /// </summary>
    public static bool TrySortOrder(IQueryCollection query, out string order, out IResult? refusal)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.TryGetValue("order", out Microsoft.Extensions.Primitives.StringValues values))
        {
            order = SectionSortOrder.Default;
            refusal = null;
            return true;
        }

        if (SectionSortOrder.TryNormalize(values.ToString(), out order))
        {
            refusal = null;
            return true;
        }

        order = SectionSortOrder.Default;
        refusal = Error(StatusCodes.Status400BadRequest, "order must be 'desc' or 'asc'");
        return false;
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
    /// is empty or malformed, or a 413 when it exceeds <paramref name="maxBytes"/>, matching the
    /// engine's existing response text.
    /// </summary>
    public static async Task<(bool Ok, JsonElement Body, IResult? Failure)> ReadJsonBodyAsync(
        HttpRequest request,
        CancellationToken cancellationToken,
        long maxBytes = DefaultMaxJsonBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[] bytes;
        using (MemoryStream buffer = new())
        {
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    return (false, default, Error(
                        StatusCodes.Status413PayloadTooLarge,
                        string.Create(CultureInfo.InvariantCulture, $"Request body exceeds the {maxBytes} byte limit")));
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

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
