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
    public AnalyticsExecutionResult Execute(string operation, JsonElement? request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        (int statusCode, JsonElement envelope) = bridge.ExecuteAnalytics(operation, request);
        return new AnalyticsExecutionResult(statusCode, envelope);
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
