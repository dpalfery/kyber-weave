using System.Globalization;
using System.Text.Json;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Contract-v1 analytics operations shared by the HTTP analytics routes and <c>kyberdash-mcp</c>
/// (<see cref="KyberBridgeEngine"/>).
/// </summary>
/// <remarks>
/// The eight operations are read-only projections of the relational KyberDash store answered by
/// <see cref="KyberBridgeEngine"/>. An unknown operation is a caller error, not a server failure,
/// so it answers <c>400</c> with a message naming the operation instead of throwing.
/// </remarks>
public class AnalyticsService : IAnalyticsService
{
    /// <summary>Longest exception text copied into an error envelope.</summary>
    private const int MaxFailureMessageChars = 500;

    /// <summary>Operations this service understands, in the order they are advertised.</summary>
    public static IReadOnlyList<string> Operations { get; } =
    [
        "status",
        "catalog",
        "query",
        "search-sessions",
        "inspect",
        "findings",
        "compare",
        "outcomes",
    ];

    private readonly KyberBridgeEngine bridge;

    /// <summary>
    /// Initializes a new <see cref="AnalyticsService"/>.
    /// </summary>
    /// <param name="bridge">Bridge engine executing each operation.</param>
    public AnalyticsService(KyberBridgeEngine bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        this.bridge = bridge;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A failing operation is reported as a contract-v1 envelope with HTTP <c>500</c> rather than an
    /// exception, because the endpoint and the MCP adapter both read the envelope: an empty <c>500</c>
    /// gives a caller nothing to act on.
    /// </remarks>
    public AnalyticsExecutionResult Execute(string operation, JsonElement? request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        try
        {
            (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics(operation, request);
            return new AnalyticsExecutionResult(statusCode, envelope);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failure(operation, ex);
        }
    }

    /// <summary>Builds the contract-v1 envelope reported when an analytics operation throws.</summary>
    /// <param name="operation">Operation that failed.</param>
    /// <param name="exception">The exception the engine raised.</param>
    /// <returns>A <c>500</c> result carrying the operation and the cause.</returns>
    public static AnalyticsExecutionResult Failure(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string cause = exception.GetType().Name + ": " + exception.Message;
        if (cause.Length > MaxFailureMessageChars)
        {
            int cut = MaxFailureMessageChars;
            if (char.IsHighSurrogate(cause[cut - 1]))
                cut--;
            cause = cause[..cut] + "…";
        }

        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"The analytics operation '{operation}' failed ({cause}).");

        JsonElement envelope = JsonSerializer.SerializeToElement(new
        {
            contractVersion = KyberBridgeEngine.ApiContractVersion,
            scope = new { },
            data = (object?)null,
            measurement = new { },
            coverage = new { },
            freshness = new { },
            evidence = Array.Empty<object>(),
            pagination = new { },
            error = new
            {
                code = "internal_error",
                message,
                hint = "The request was valid; the engine could not complete it. Check the engine log and retry.",
            },
        });

        return new AnalyticsExecutionResult(500, envelope);
    }

    /// <summary>Returns whether <paramref name="operation"/> is one of the advertised operations.</summary>
    /// <param name="operation">Candidate operation name, matched case-insensitively.</param>
    /// <returns><see langword="true"/> when the operation is supported.</returns>
    public static bool IsSupported(string? operation) =>
        !string.IsNullOrWhiteSpace(operation)
        && Operations.Contains(operation.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds the envelope returned for an unsupported operation.</summary>
    /// <param name="operation">Operation the caller asked for.</param>
    /// <returns>A <c>400</c> result naming the operation and listing the supported ones.</returns>
    public static AnalyticsExecutionResult Unsupported(string operation)
    {
        JsonElement envelope = JsonSerializer.SerializeToElement(new
        {
            operation,
            error = string.Create(CultureInfo.InvariantCulture, $"Unknown analytics operation '{operation}'."),
            operations = Operations,
        });

        return new AnalyticsExecutionResult(400, envelope);
    }
}
