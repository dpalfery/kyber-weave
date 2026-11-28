namespace KyberDash.Api.Endpoints;

/// <summary>Which OTLP signal an export path carries, so one handler serves all three.</summary>
internal enum OtlpSignalKind
{
    /// <summary><c>/v1/traces</c>; admitted through <c>AdmitTraces</c>.</summary>
    Traces,

    /// <summary><c>/v1/logs</c>; admitted through <c>AdmitLogs</c>.</summary>
    Logs,

    /// <summary><c>/v1/metrics</c>; admitted through <c>AdmitMetrics</c>.</summary>
    Metrics,
}
