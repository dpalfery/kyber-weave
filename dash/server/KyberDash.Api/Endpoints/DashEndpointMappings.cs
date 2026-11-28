using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using KyberDash.Application;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KyberDash.Api.Endpoints;

/// <summary>
/// Maps the <c>/api/kyber/*</c> REST surface, the <c>/api/events</c> SSE stream, and the SPA
/// fallback onto the <c>KyberDash.Contracts.Services</c> interfaces.
/// </summary>
/// <remarks>
/// Every handler is transport only: it reads the request, calls one application service, and
/// shapes the response. Repository reads are synchronous, so those calls run on a worker thread
/// rather than blocking an ASP.NET Core request thread.
/// </remarks>
public static class DashEndpointMappings
{
    private const int DefaultSessionContentBudget = 32_768;
    private const string MutationForbiddenMessage =
        "Mutating routes accept same-machine loopback requests without browser Origin headers only";
    private const string PagingInvalidMessage =
        "limit and page must be positive whole numbers and offset a non-negative whole number";

    private const string OffsetNotPageStartMessage =
        "offset must fall on the start of a page (a multiple of limit); these routes page by page and limit";

    private static readonly string[] PostAnalyticsOperations =
    [
        "query",
        "search-sessions",
        "inspect",
        "findings",
        "compare",
        "outcomes",
    ];

    /// <summary>Maps every dashboard REST endpoint, the SSE event stream, and the SPA fallback.</summary>
    public static void MapDashApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/events", StreamEventsAsync);

        RouteGroupBuilder kyber = endpoints.MapGroup("/api/kyber");
        kyber.MapGet("/sessions", Sessions);
        kyber.MapGet("/session", Sessions);
        kyber.MapGet("/session/{id}/summary", SessionSummary);
        kyber.MapGet("/session/{id}/charts", SessionCharts);
        kyber.MapGet("/session/{id}/requests", SessionRequests);
        kyber.MapGet("/session/{id}/reconciliation", SessionReconciliation);
        kyber.MapGet("/session/{id}/problems", SessionProblems);
        kyber.MapGet("/session/{id}/tools", SessionTools);
        kyber.MapGet("/session/{id}/servers", SessionServers);
        kyber.MapGet("/session/{id}/skills", SessionSkills);
        kyber.MapGet("/session/{id}/kw-skills", SessionKwSkills);
        kyber.MapGet("/session/{id}/timeline", SessionTimeline);
        kyber.MapGet("/session/{id}/content", SessionContent);
        kyber.MapGet("/session/{id}/subagents", SessionSubagents);
        kyber.MapGet("/session/{id}/turn/{index}", TurnFigures);
        kyber.MapGet("/session/{id}/turn/{index}/content", TurnContent);
        kyber.MapGet("/span/{id}/attributes", SpanAttributes);
        kyber.MapGet("/session/{sessionKey}/stored-fields", StoredFieldsInventory);
        kyber.MapGet("/session/{sessionKey}/stored-fields/{table}", StoredFieldRows);

        kyber.MapGet("/compare", Compare);
        kyber.MapGet("/compare/runs", CompareRuns);

        kyber.MapGet("/quarantine", Quarantine);
        kyber.MapGet("/quarantine/{spanId}", QuarantineDetail);
        kyber.MapGet("/problems", Problems);
        kyber.MapGet("/problem/{id}", ProblemDetail);

        kyber.MapGet("/meta", Meta);
        kyber.MapGet("/version", Version);
        kyber.MapGet("/coverage", Coverage);
        kyber.MapGet("/report", Report);
        kyber.MapGet("/findings", Findings);
        kyber.MapGet("/finding/{findingId}", FindingDetail);
        kyber.MapGet("/predictions", Predictions);
        kyber.MapPost("/predictions", RecordPredictionAsync);
        kyber.MapGet("/calibration", Calibration);
        kyber.MapGet("/harnesses", Harnesses);
        kyber.MapGet("/harness/{id}", HarnessDetail);
        kyber.MapGet("/runs", Runs);
        kyber.MapGet("/run/{id}", RunDetail);
        kyber.MapGet("/timeline", Timeline);

        kyber.MapGet("/review/status", ReviewStatus);
        kyber.MapPost("/review", Review);

        kyber.MapGet("/model-catalog", ModelCatalog);
        kyber.MapPost("/model-catalog/refresh", ModelCatalogRefresh);

        kyber.MapGet("/capture", CaptureStatus);
        kyber.MapPost("/capture", CaptureConfigureAsync);

        kyber.MapGet("/settings", Settings);
        kyber.MapPut("/settings", UpdateSettingsAsync);

        kyber.MapGet("/jobs", JobsStatus);
        kyber.MapPost("/refresh", RefreshAsync);
        kyber.MapPost("/import-history", ImportHistoryAsync);
        kyber.MapPost("/clean", CleanAsync);

        kyber.MapGet("/analytics/status", AnalyticsStatus);
        kyber.MapGet("/analytics/catalog", AnalyticsCatalog);

        foreach (string operation in PostAnalyticsOperations)
        {
            string captured = operation;
            kyber.MapPost($"/analytics/{captured}", (HttpRequest request, IAnalyticsService analytics, CancellationToken cancellationToken) =>
                AnalyticsPostAsync(captured, request, analytics, cancellationToken));
        }

        // Explicit patterns, never the parameterless MapFallback. That overload registers
        // `{*path:nonfile}`, which excludes every path carrying a file extension: `/assets/x.js`
        // and `/favicon.ico` then match no endpoint at all, so the SPA's own bundle 404s and the
        // browser never boots the app. The catch-all below carries the intent deliberately; it is
        // still a fallback (order int.MaxValue), so `/api/*` and `/healthz` answer first. A missing
        // file or asset is a 404, as ServeSpa enforces, not a rewritten app shell.
        endpoints.MapFallback("/{*path}", ServeSpa);
    }

    private static async Task StreamEventsAsync(
        HttpContext context,
        IOtlpCollectorService collector,
        CancellationToken cancellationToken) =>
        // The stream carries one `update` event per store change until the client goes away, so it
        // must not return after the preamble: closing there leaves the client with a single comment
        // and every reconnect sees nothing.
        await WriteEventStreamAsync(context, collector, cancellationToken).ConfigureAwait(false);

    /// <summary>Writes the SSE preamble, then one <c>update</c> event per flushed batch.</summary>
    /// <remarks>
    /// Once the preamble is flushed the response is committed: cancelling the client is the normal
    /// way this ends. A stream that has not started writing must not fail the application, so
    /// nothing after the preamble ever reaches the request pipeline.
    /// </remarks>
    private static async Task WriteEventStreamAsync(
        HttpContext context,
        IOtlpCollectorService collector,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        // Latest-only: a dashboard needs to know the store moved, not how many times.
        Channel<bool> updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        void OnStoreUpdated(object? sender, EventArgs args) => updates.Writer.TryWrite(true);

        collector.StoreUpdated += OnStoreUpdated;
        try
        {
            await context.Response.WriteAsync(": connected\n\n", Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                await updates.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                await context.Response.WriteAsync(
                    "event: update\ndata: {\"type\":\"store_updated\"}\n\n",
                    Encoding.UTF8,
                    cancellationToken).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        finally
        {
            collector.StoreUpdated -= OnStoreUpdated;
        }
    }

    private static IResult Sessions(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, IResult? refusal) = ReadPageWindow(request, KyberBridgeEngine.DefaultSessionPageLimit);
        if (refusal is not null)
            return refusal;

        string? harness = DashHttp.NormalizeFilter(DashHttp.OptionalQueryString(request.Query, "harness"));
        string? search = DashHttp.OptionalQueryString(request.Query, "q");
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetSessionPage(page, limit, harness, search));
    }

    private static IResult SessionSubagents(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, IResult? refusal) = ReadPageWindow(request, KyberBridgeEngine.DefaultSubagentPageLimit, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetSubagentPage(id, page, limit));
    }

    /// <summary>
    /// Reads the page window the sessions routes share — <c>limit</c> and <c>page</c>, with
    /// <c>offset</c> accepted only where it starts a page — or the response that refuses it: 404
    /// for a blank session id, 400 for a malformed window. The default page size is also the
    /// largest the route serves (100 sessions, 25 section rows), so a larger <c>limit</c> is clamped.
    /// </summary>
    /// <param name="request">Incoming request carrying the query string.</param>
    /// <param name="defaultLimit">Rows per page when the request names no limit, and the most it may name.</param>
    /// <param name="sessionId">The session the route reads, or <see langword="null"/> for the sessions list.</param>
    /// <returns>The clamped limit, the one-based page, and the refusal to answer, or <see langword="null"/> when the window is valid.</returns>
    private static (int Limit, int Page, IResult? Refusal) ReadPageWindow(HttpRequest request, int defaultLimit, string? sessionId = null)
    {
        if (sessionId is not null && string.IsNullOrWhiteSpace(sessionId))
            return (defaultLimit, 1, DashHttp.Error(StatusCodes.Status404NotFound, "Session not found"));

        (int limit, int _, int page) = DashHttp.Pagination(request.Query, defaultLimit, out bool invalid, maxLimit: defaultLimit);
        if (invalid)
            return (limit, page, DashHttp.Error(StatusCodes.Status400BadRequest, PagingInvalidMessage));

        if (!OffsetStartsThePage(request.Query, limit, page))
            return (limit, page, DashHttp.Error(StatusCodes.Status400BadRequest, OffsetNotPageStartMessage));

        return (limit, page, null);
    }

    /// <summary>
    /// Reads the strict page and sort window for all nine session detail section routes:
    /// <c>limit</c> in <c>{25, 50, 100}</c> (default <c>25</c>), <c>page</c> &gt;= 1, optional
    /// <c>offset</c> aligned to page start, and trimmed case-insensitive <c>order</c> in
    /// <c>{"desc", "asc"}</c> (default <c>"desc"</c>).
    /// </summary>
    private static (int Limit, int Page, string Order, IResult? Refusal) ReadSectionPageWindow(HttpRequest request, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return (KyberBridgeEngine.DefaultSectionPageLimit, 1, "desc", DashHttp.Error(StatusCodes.Status404NotFound, "Session not found"));

        if (!DashHttp.TrySectionLimit(request.Query, KyberBridgeEngine.DefaultSectionPageLimit, out int sectionLimit, out IResult? limitRefusal))
            return (sectionLimit, 1, "desc", limitRefusal);

        (int limit, int _, int page) = DashHttp.Pagination(request.Query, KyberBridgeEngine.DefaultSectionPageLimit, out bool invalid, maxLimit: 100);
        if (invalid)
            return (limit, page, "desc", DashHttp.Error(StatusCodes.Status400BadRequest, PagingInvalidMessage));

        if (!OffsetStartsThePage(request.Query, limit, page))
            return (limit, page, "desc", DashHttp.Error(StatusCodes.Status400BadRequest, OffsetNotPageStartMessage));

        if (!DashHttp.TrySortOrder(request.Query, out string order, out IResult? orderRefusal))
            return (limit, page, order, orderRefusal);

        return (limit, page, order, null);
    }

    /// <summary>
    /// Whether a request's <c>offset</c>, when it carries one, is the first row of the page
    /// <paramref name="page"/> names. The sessions routes page by <c>page</c> and <c>limit</c>:
    /// any other offset would be snapped to a page start and answer a window the caller did not
    /// ask for, so it is refused instead.
    /// </summary>
    /// <param name="query">The request's query string.</param>
    /// <param name="limit">Rows per page, after clamping.</param>
    /// <param name="page">The one-based page the request resolves to.</param>
    /// <returns><see langword="true"/> when the request carries no offset or its offset starts that page.</returns>
    private static bool OffsetStartsThePage(IQueryCollection query, int limit, int page) =>
        DashHttp.OptionalNonNegativeInt(query, "offset") is not int rawOffset
        || rawOffset == (long)(page - 1) * limit;

    private static IResult SessionSummary(string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetSessionSummary(id), "Session not found");
    }

    private static IResult SessionCharts(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionCharts(id, page, limit, order) is { } charts
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, charts)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionRequests(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionRequests(id, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionReconciliation(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionReconciliation(id, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionProblems(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionProblems(id, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionTools(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionTools(id, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionServers(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionServers(id, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionSkills(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionSkills(id, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionKwSkills(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionKwSkills(id, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    /// <summary>
    /// Serves <c>GET /api/kyber/session/{id}/timeline</c>: one page of one level of the session's
    /// timeline tree. The <c>parent</c> query value arrives URL-encoded when it names a synthetic
    /// container (<c>%28auxiliary%29</c>), so it is read through the query collection, which hands
    /// the decoded id to the service.
    /// </summary>
    private static IResult SessionTimeline(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, string order, IResult? refusal) = ReadSectionPageWindow(request, id);
        if (refusal is not null)
            return refusal;

        string? parent = DashHttp.OptionalQueryString(request.Query, "parent");
        cancellationToken.ThrowIfCancellationRequested();
        return query.GetSessionTimeline(id, parent, page, limit, order) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session not found");
    }

    private static IResult SessionContent(HttpRequest request, string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        int? turn = DashHttp.OptionalNonNegativeInt(request.Query, "turn");
        string? spanId = DashHttp.OptionalQueryString(request.Query, "span");
        string? part = DashHttp.OptionalQueryString(request.Query, "part");
        bool full = DashHttp.IsTruthy(DashHttp.OptionalQueryString(request.Query, "full"));
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetSessionContent(id, turn, spanId, part, full), "Session not found");
    }

    private static IResult TurnFigures(string id, string index, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int turnIndex))
            return DashHttp.Error(StatusCodes.Status400BadRequest, "Invalid turn index");

        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetTurnFigures(id, turnIndex), "Turn not found");
    }

    private static IResult TurnContent(
        HttpRequest request,
        string id,
        string index,
        IDashboardQueryService query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int turnIndex))
            return DashHttp.Error(StatusCodes.Status400BadRequest, "Invalid turn index");

        // The response budget caps what this endpoint may return, so an oversized request cannot
        // talk it into a bigger body. A budget that is present but malformed is a bad request, not
        // a request for the default.
        int? requestedBudget = DashHttp.OptionalPositiveInt(request.Query, "budget", out bool invalidBudget);
        if (invalidBudget)
            return DashHttp.Error(StatusCodes.Status400BadRequest, "budget must be a positive whole number");

        int budget = Math.Clamp(requestedBudget ?? DefaultSessionContentBudget, 1, DefaultSessionContentBudget);
        bool full = DashHttp.IsTruthy(DashHttp.OptionalQueryString(request.Query, "full"));
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetTurnContent(id, turnIndex, budget, full), "Turn not found");
    }

    private static IResult SpanAttributes(string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetSpanAttributes(id), "Span not found");
    }

    private static IResult StoredFieldsInventory(string sessionKey, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetStoredFieldsInventory(sessionKey), "Session not found");
    }

    private static IResult StoredFieldRows(HttpRequest request, string sessionKey, string table, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        (int limit, int page, IResult? refusal) = ReadPageWindow(request, KyberBridgeEngine.DefaultSectionPageLimit, sessionKey);
        if (refusal is not null)
            return refusal;

        cancellationToken.ThrowIfCancellationRequested();
        return query.GetStoredFieldRows(sessionKey, table, page, limit) is { } payload
                ? DashHttp.NoStoreJson(StatusCodes.Status200OK, payload)
                : DashHttp.Error(StatusCodes.Status404NotFound, "Session or table not found");
    }

    private static IResult Compare(IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetCompare());
    }

    private static IResult CompareRuns(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? runA = DashHttp.OptionalQueryString(request.Query, "runA") ?? DashHttp.OptionalQueryString(request.Query, "a");
        string? runB = DashHttp.OptionalQueryString(request.Query, "runB") ?? DashHttp.OptionalQueryString(request.Query, "b");
        if (string.IsNullOrEmpty(runA) || string.IsNullOrEmpty(runB))
            return DashHttp.Error(StatusCodes.Status400BadRequest, "Missing runA or runB");

        // Recommendation history is never caller-supplied (issue #190): reject it rather than ignore it.
        if (request.Query.ContainsKey("completedPairCount"))
        {
            return DashHttp.Error(
                StatusCodes.Status400BadRequest,
                "completedPairCount is not accepted: recommendation history is not caller-supplied (issue #190)");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetCompareRuns(runA, runB), "Run not found");
    }

    private static IResult Quarantine(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? harness = DashHttp.NormalizeFilter(DashHttp.OptionalQueryString(request.Query, "harness"));
        (int limit, int offset, int page) = DashHttp.Pagination(request.Query, defaultLimit: 200, out bool invalidPaging);
        if (invalidPaging)
            return DashHttp.Error(StatusCodes.Status400BadRequest, PagingInvalidMessage);

        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetQuarantinePage(harness, limit, offset, page));
    }

    private static IResult QuarantineDetail(string spanId, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetQuarantineDetail(spanId), "Quarantine entry not found");
    }

    private static IResult Problems(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? harness = DashHttp.NormalizeFilter(DashHttp.OptionalQueryString(request.Query, "harness"));
        (int limit, int offset, int page) = DashHttp.Pagination(request.Query, defaultLimit: 200, out bool invalidPaging);
        if (invalidPaging)
            return DashHttp.Error(StatusCodes.Status400BadRequest, PagingInvalidMessage);

        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetProblemsPage(harness, limit, offset, page));
    }

    private static IResult ProblemDetail(string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetProblemDetail(id), "Problem not found");
    }

    private static IResult Meta(IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetMeta());
    }

    private static IResult Version(IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetVersion());
    }

    private static IResult Coverage(IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetCoverage());
    }

    private static IResult Report(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? harness = DashHttp.NormalizeFilter(DashHttp.OptionalQueryString(request.Query, "harness"));
        string? sessionId = DashHttp.OptionalQueryString(request.Query, "session")?.Trim();
        string? runId = DashHttp.OptionalQueryString(request.Query, "run")?.Trim();
        int? parsedLimit = DashHttp.OptionalPositiveInt(request.Query, "limit", out bool invalidLimit);
        if (invalidLimit)
            return DashHttp.Error(StatusCodes.Status400BadRequest, "limit must be a positive whole number");

        int limit = Math.Clamp(parsedLimit ?? 50, 1, 200);
        int? parsedDays = DashHttp.OptionalPositiveInt(request.Query, "days", out bool invalidDays);
        if (invalidDays)
            return DashHttp.Error(StatusCodes.Status400BadRequest, "days must be a positive whole number");

        int days = parsedDays ?? 7;

        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetReport(harness, sessionId, runId, limit, days));
    }

    private static IResult Findings(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? runId = DashHttp.OptionalQueryString(request.Query, "runId");
        string? sessionId = DashHttp.OptionalQueryString(request.Query, "sessionId");
        string? detector = DashHttp.OptionalQueryString(request.Query, "detector");
        string? harness = DashHttp.OptionalQueryString(request.Query, "harness");
        (int limit, int offset, int _) = DashHttp.Pagination(request.Query, defaultLimit: 100, out bool invalidPaging);
        if (invalidPaging)
            return DashHttp.Error(StatusCodes.Status400BadRequest, PagingInvalidMessage);

        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetFindings(runId, sessionId, detector, harness, limit, offset));
    }

    private static IResult FindingDetail(string findingId, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        Finding? finding = query.GetFindingById(findingId);
        return finding is not null
            ? DashHttp.NoStoreJson(StatusCodes.Status200OK, finding)
            : DashHttp.Error(StatusCodes.Status404NotFound, "Finding not found");
    }

    private static IResult Predictions(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? runId = DashHttp.OptionalQueryString(request.Query, "runId");
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetPredictions(runId));
    }

    private static async Task<IResult> RecordPredictionAsync(
        HttpRequest request,
        IDashboardQueryService query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        (bool ok, JsonElement body, IResult? failure) = await DashHttp.ReadJsonBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return JsonBodyFailure(failure);

        cancellationToken.ThrowIfCancellationRequested();
        if (!query.TryRecordPrediction(body, out PredictionRow? created, out string? error))
            return DashHttp.Error(StatusCodes.Status400BadRequest, error ?? "Invalid prediction");

        // A successful record always carries a row; a query stub may return none, which the
        // endpoint has always answered as a created response with a null body.
        return created is { } row
            ? DashHttp.NoStoreJson(StatusCodes.Status201Created, row)
            : DashHttp.NoStoreJson(StatusCodes.Status201Created, JsonSerializer.SerializeToElement<object?>(null));
    }

    /// <summary>
    /// Returns the response a failed body read carries. The read reports failure only with a
    /// response, so a missing one is a programming error rather than a client error.
    /// </summary>
    private static IResult JsonBodyFailure(IResult? failure) =>
        failure ?? throw new InvalidOperationException("A failed JSON body read must carry an error response.");

    private static IResult Calibration(IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetCalibration());
    }

    private static IResult Harnesses(IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetHarnesses());
    }

    private static IResult HarnessDetail(string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetHarnessById(id), "Harness not found");
    }

    private static IResult Runs(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? harness = DashHttp.OptionalQueryString(request.Query, "harness");
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetRuns(harness));
    }

    private static IResult RunDetail(string id, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.FromNullableJson(query.GetRunById(id), "Run not found");
    }

    private static IResult Timeline(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        string? sessionId = DashHttp.OptionalQueryString(request.Query, "sessionId");
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetTimeline(sessionId));
    }

    /// <summary>
    /// Reports the review state. A review is a projection pass, which is instantaneous once the
    /// store is projected, so the engine never reports an in-flight review.
    /// </summary>
    private static IResult ReviewStatus() =>
        DashHttp.NoStoreJson(StatusCodes.Status200OK, new { state = "idle" });

    private static IResult Review(HttpRequest request, IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(query);

        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        cancellationToken.ThrowIfCancellationRequested();
        query.EnsureProjected();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, new { state = "idle", ok = true });
    }

    private static IResult ModelCatalog(IDashboardQueryService query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, query.GetModelCatalog());
    }

    private static IResult ModelCatalogRefresh(
        HttpRequest request,
        IDashboardQueryService query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        return ModelCatalog(query, cancellationToken);
    }

    private static IResult CaptureStatus(ICaptureService capture, DashRuntimeSettings runtime)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(runtime);

        int? otlpPort = runtime.CaptureStatusOtlpPort();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, capture.GetCaptureStatus(runtime.StateDirectory, otlpPort));
    }

    private static async Task<IResult> CaptureConfigureAsync(
        HttpRequest request,
        ICaptureService capture,
        ISettingsService settings,
        DashRuntimeSettings runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(runtime);

        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        (bool ok, JsonElement body, IResult? failure) = await DashHttp.ReadJsonBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return JsonBodyFailure(failure);

        try
        {
            capture.ConfigureCapture(body, runtime.StateDirectory);
        }
        catch (DashValidationException ex)
        {
            return DashHttp.Error(StatusCodes.Status400BadRequest, ex.Message);
        }

        SettingsView updated = settings.GetSettings();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, new { ok = true, receiverHosted = updated.ReceiverHosted });
    }

    private static IResult Settings(ISettingsService settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, settings.GetSettings());
    }

    private static async Task<IResult> UpdateSettingsAsync(
        HttpRequest request,
        ISettingsService settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);

        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        (bool ok, JsonElement patch, IResult? failure) = await DashHttp.ReadJsonBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return JsonBodyFailure(failure);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DashHttp.NoStoreJson(StatusCodes.Status200OK, settings.UpdateSettings(patch));
        }
        catch (DashValidationException ex)
        {
            return DashHttp.Error(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static IResult JobsStatus(IJobSchedulerService jobs, ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(settings);

        JobHostStatus status = jobs.GetStatus();
        return DashHttp.NoStoreJson(StatusCodes.Status200OK, new
        {
            refresh = new
            {
                state = status.State,
                lastSuccessAt = status.LastSuccessAt,
                lastFailure = status.LastFailure,
                nextDueAt = status.NextDueAt,
                failedUnits = status.LastFailedUnits,
            },
            paused = status.Paused || settings.GetSettings().JobsPaused,
            hostedElsewhere = status.HostedElsewhere,
        });
    }

    private static async Task<IResult> RefreshAsync(HttpRequest request, IJobSchedulerService jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        JobOutcome outcome = jobs.TriggerRefresh();
        return JobOutcomeResult(outcome);
    }

    private static async Task<IResult> ImportHistoryAsync(HttpRequest request, IJobSchedulerService jobs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jobs);

        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        JobOutcome outcome = jobs.TriggerImportHistory();
        return JobOutcomeResult(outcome);
    }

    private static IResult JobOutcomeResult(JobOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        int statusCode = outcome.Outcome switch
        {
            "succeeded" or "partial" => StatusCodes.Status200OK,
            "busy" or "declined" or "hosted-elsewhere" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
        return DashHttp.NoStoreJson(statusCode, outcome);
    }

    private static async Task<IResult> CleanAsync(
        HttpRequest request,
        ICleanService clean,
        IOtlpCollectorService collector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clean);
        ArgumentNullException.ThrowIfNull(collector);

        if (!DashHttp.IsAllowedMutation(request))
            return DashHttp.Error(StatusCodes.Status403Forbidden, MutationForbiddenMessage);

        (bool ok, JsonElement body, IResult? failure) = await DashHttp.ReadJsonBodyAsync(request, cancellationToken).ConfigureAwait(false);
        if (!ok)
            return JsonBodyFailure(failure);

        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("confirm", out JsonElement confirm)
            || confirm.ValueKind != JsonValueKind.True)
        {
            return DashHttp.Error(StatusCodes.Status400BadRequest, "clean requires explicit confirm: true");
        }

        CleanScope scope = ParseCleanScope(body);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CleanReport report = clean.CleanDatabase(scope, collector.PauseIngestion, collector.ResumeIngestion);
            return DashHttp.NoStoreJson(StatusCodes.Status200OK, report);
        }
        catch (ArgumentException ex)
        {
            return DashHttp.Error(StatusCodes.Status400BadRequest, ex.Message);
        }
    }

    private static CleanScope ParseCleanScope(JsonElement json)
    {
        bool all = json.TryGetProperty("all", out JsonElement allElement) && allElement.ValueKind == JsonValueKind.True;
        List<string>? harnesses = null;
        if (json.TryGetProperty("harnesses", out JsonElement harnessesElement) && harnessesElement.ValueKind == JsonValueKind.Array)
        {
            harnesses = [];
            foreach (JsonElement item in harnessesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is string harness)
                    harnesses.Add(harness);
            }
        }

        int? reingestWeeks = null;
        if (json.TryGetProperty("reingestWeeks", out JsonElement weeksElement)
            && weeksElement.ValueKind == JsonValueKind.Number
            && weeksElement.TryGetInt32(out int weeks))
        {
            reingestWeeks = weeks;
        }

        return new CleanScope(All: all, Harnesses: harnesses, ReingestWeeks: reingestWeeks);
    }

    private static IResult AnalyticsStatus(IAnalyticsService analytics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        cancellationToken.ThrowIfCancellationRequested();
        return AnalyticsResult("status", analytics, request: null);
    }

    private static IResult AnalyticsCatalog(IAnalyticsService analytics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analytics);
        cancellationToken.ThrowIfCancellationRequested();
        return AnalyticsResult("catalog", analytics, request: null);
    }

    private static async Task<IResult> AnalyticsPostAsync(
        string operation,
        HttpRequest request,
        IAnalyticsService analytics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(analytics);

        JsonElement body;
        if (request.ContentLength == 0)
        {
            body = JsonSerializer.SerializeToElement(new { });
        }
        else
        {
            (bool ok, JsonElement parsed, IResult? failure) = await DashHttp.ReadJsonBodyAsync(request, cancellationToken).ConfigureAwait(false);
            if (!ok)
                return JsonBodyFailure(failure);

            body = parsed;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AnalyticsResult(operation, analytics, body);
    }

    /// <summary>
    /// Runs one analytics operation and shapes the answer. A service that throws is answered as a
    /// JSON error envelope with HTTP 500: an empty 500 leaves the caller nothing to report.
    /// </summary>
    private static IResult AnalyticsResult(string operation, IAnalyticsService analytics, JsonElement? request)
    {
        try
        {
            AnalyticsExecutionResult result = analytics.Execute(operation, request);
            return DashHttp.NoStoreJson(result.StatusCode, result.Envelope);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnalyticsExecutionResult failure = AnalyticsService.Failure(operation, ex);
            return DashHttp.NoStoreJson(failure.StatusCode, failure.Envelope);
        }
    }

    private static IResult ServeSpa(
        HttpContext context,
        IWebAssetService assets,
        DashRuntimeSettings runtime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(runtime);

        string path = context.Request.Path.HasValue ? context.Request.Path.Value : "/";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase)
            || string.Equals(OtlpCollector.HealthzPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return DashHttp.Error(StatusCodes.Status404NotFound, "Not found");
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            return DashHttp.Error(StatusCodes.Status405MethodNotAllowed, "Method Not Allowed");

        WebAssetDocument? document;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            document = assets.TryResolveAsset(path, runtime.DashboardDirectory);

            // A request for a file must be answered with that file or with 404: the asset service
            // answers an unknown key with index.html, and handing HTML to an import of
            // `/assets/index-*.js` stops the dashboard before it renders. A client-side route has no
            // file name, so it still receives the app shell below.
            if (document is not null
                && IsFileName(path)
                && !string.Equals(document.ResolvedPath, NormalizeAssetKey(path), StringComparison.Ordinal))
            {
                return DashHttp.Error(StatusCodes.Status404NotFound, "Not found");
            }
        }
        catch (UriFormatException)
        {
            // A malformed escape such as /%ZZ is the client's error, not a fault of this host.
            return DashHttp.Error(StatusCodes.Status400BadRequest, "Malformed request path");
        }

        if (document is null)
            return NotBuiltResult();

        // The app shell is a document wherever it is served: at "/", as index.html, and on every
        // client-side route (/session/s/turn/0), where the asset service answers with index.html.
        // It must never be cached, or an old shell keeps naming asset hashes a later build dropped.
        bool isDocument = string.Equals(path, "/", StringComparison.Ordinal)
            || string.Equals(Path.GetExtension(path), ".html", StringComparison.OrdinalIgnoreCase)
            || (!IsFileName(path) && document.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
        context.Response.Headers.CacheControl = isDocument
            ? "no-store"
            : "public, max-age=31536000, immutable";

        string contentType = document.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            contentType = DashHttp.ContentTypeForPath(isDocument ? "index.html" : path) ?? "application/octet-stream";

        return Results.Bytes(document.Content, contentType);
    }

    /// <summary>True when the request path names a file rather than a client-side route.</summary>
    private static bool IsFileName(string path)
    {
        string lastSegment = path.TrimEnd('/');
        int slash = lastSegment.LastIndexOf('/');
        if (slash >= 0)
            lastSegment = lastSegment[(slash + 1)..];
        return lastSegment.Length > 0 && lastSegment.IndexOf('.', StringComparison.Ordinal) > 0;
    }

    /// <summary>Reduces a request path to the asset key the repository resolves it with.</summary>
    private static string NormalizeAssetKey(string path) => Uri.UnescapeDataString(path).TrimStart('/');

    private static IResult NotBuiltResult()
    {
        const string Html =
            "<!doctype html><meta charset=\"utf-8\">"
            + "<body style=\"font-family:system-ui;background:#0a0a0b;color:#e7e7ea;padding:48px;line-height:1.6\">"
            + "<div id=\"root\"><h2>Dashboard not built yet</h2>"
            + "<p>Run `npm --prefix dash run build` to produce the dashboard bundle. "
            + "The CLI keeps serving the live data API in the meantime.</p></div></body>";

        return Results.Content(Html, "text/html; charset=utf-8");
    }
}
