using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KyberDash.Api.Endpoints;

/// <summary>
/// Maps the OTLP/HTTP receiver surface: the <c>/healthz</c> probe, the loopback-only pause and
/// resume admin routes, and the three signal export paths.
/// </summary>
/// <remarks>
/// The receiver accepts Protobuf and JSON alike. Decoding is the collector's job, so a malformed
/// payload surfaces as <see cref="OtlpDecodeException"/> here and becomes a 400, and an oversize
/// one as <see cref="OtlpPayloadTooLargeException"/> and becomes a 413.
/// </remarks>
public static class OtlpEndpointMappings
{
    private const long DefaultMaxBodyBytes = 64L * 1024 * 1024;

    /// <summary>Maps <c>/healthz</c>, <c>/v1/admin/pause</c>, <c>/v1/admin/resume</c>, and the signal paths.</summary>
    public static void MapOtlpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(OtlpCollector.HealthzPath, (IOtlpCollectorService collector) =>
            Results.Json(new
            {
                ok = true,
                paused = collector.IsPaused,
                pendingBytes = collector.PendingBytes,
                pendingCount = collector.PendingCount,
            }));

        endpoints.MapPost("/v1/admin/pause", HandlePauseAsync);
        endpoints.MapPost("/v1/admin/resume", HandleResumeAsync);

        endpoints.MapPost(OtlpCollector.TracesPath, (HttpContext context, IOtlpCollectorService collector, DashRuntimeSettings runtime, CancellationToken cancellationToken) =>
            AdmitAsync(context, collector, runtime, OtlpSignalKind.Traces, cancellationToken));

        endpoints.MapPost(OtlpCollector.LogsPath, (HttpContext context, IOtlpCollectorService collector, DashRuntimeSettings runtime, CancellationToken cancellationToken) =>
            AdmitAsync(context, collector, runtime, OtlpSignalKind.Logs, cancellationToken));

        endpoints.MapPost(OtlpCollector.MetricsPath, (HttpContext context, IOtlpCollectorService collector, DashRuntimeSettings runtime, CancellationToken cancellationToken) =>
            AdmitAsync(context, collector, runtime, OtlpSignalKind.Metrics, cancellationToken));
    }

    private static async Task<IResult> HandlePauseAsync(HttpRequest request, IOtlpCollectorService collector)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(collector);
        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, "admin routes accept loopback requests without browser Origin headers only");

        await Task.Run(collector.PauseIngestion).ConfigureAwait(false);
        return Results.Json(new { paused = collector.IsPaused });
    }

    private static async Task<IResult> HandleResumeAsync(HttpRequest request, IOtlpCollectorService collector)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(collector);
        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, "admin routes accept loopback requests without browser Origin headers only");

        await Task.Run(collector.ResumeIngestion).ConfigureAwait(false);
        return Results.Json(new { paused = collector.IsPaused });
    }

    private static async Task<IResult> AdmitAsync(
        HttpContext context,
        IOtlpCollectorService collector,
        DashRuntimeSettings runtime,
        OtlpSignalKind signal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(collector);
        ArgumentNullException.ThrowIfNull(runtime);

        if (collector.IsPaused)
        {
            return DashHttp.JsonWithHeaders(
                StatusCodes.Status503ServiceUnavailable,
                new { error = new { code = "OTLP_PAUSED", message = "receiver paused for a database clean; retry" } },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["Retry-After"] = "5" });
        }

        string? mediaType = ParseMediaType(context.Request.ContentType);
        bool isJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        bool isProtobuf = string.Equals(mediaType, "application/x-protobuf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/protobuf", StringComparison.OrdinalIgnoreCase);
        if (mediaType is not { } contentType || (!isJson && !isProtobuf))
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        code = "OTLP_UNSUPPORTED_MEDIA_TYPE",
                        message = $"content type {mediaType ?? "(none)"} is not supported: use application/json or application/x-protobuf",
                    },
                },
                statusCode: StatusCodes.Status415UnsupportedMediaType);
        }

        long maxBodyBytes = runtime.MaxOtlpBodyBytes > 0 ? runtime.MaxOtlpBodyBytes : DefaultMaxBodyBytes;
        byte[] body;
        try
        {
            body = await ReadBoundedBodyAsync(context.Request, maxBodyBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OtlpPayloadTooLargeException ex)
        {
            return PayloadTooLarge(ex.Message);
        }

        try
        {
            IngestAdmissionResult admission = signal switch
            {
                OtlpSignalKind.Traces => await collector.AdmitTracesAsync(body, contentType, cancellationToken).ConfigureAwait(false),
                OtlpSignalKind.Logs => await collector.AdmitLogsAsync(body, contentType, cancellationToken).ConfigureAwait(false),
                _ => await collector.AdmitMetricsAsync(body, contentType, cancellationToken).ConfigureAwait(false),
            };
            return DashHttp.OtlpAdmission(admission);
        }
        catch (OtlpPayloadTooLargeException ex)
        {
            return PayloadTooLarge(ex.Message);
        }
        catch (OtlpDecodeException ex)
        {
            return Results.Json(
                new { error = new { code = "OTLP_MALFORMED_PAYLOAD", message = ex.Message } },
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult PayloadTooLarge(string message) =>
        Results.Json(
            new { error = new { code = "OTLP_PAYLOAD_TOO_LARGE", message } },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// Reads the request body, refusing anything longer than <paramref name="maxBytes"/> before it is
    /// buffered any further.
    /// </summary>
    private static async Task<byte[]> ReadBoundedBodyAsync(HttpRequest request, long maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength is { } declared && declared > maxBytes)
        {
            throw new OtlpPayloadTooLargeException(
                string.Create(CultureInfo.InvariantCulture, $"OTLP payload of {declared} bytes exceeds the {maxBytes} byte limit."));
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81_920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new OtlpPayloadTooLargeException(
                    string.Create(CultureInfo.InvariantCulture, $"OTLP payload exceeds the {maxBytes} byte limit."));
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    private static string? ParseMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return null;

        int semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }
}
