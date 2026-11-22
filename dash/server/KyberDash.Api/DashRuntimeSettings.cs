using System.Globalization;
using KyberDash.Contracts.Services;

namespace KyberDash.Api;

/// <summary>
/// Transport settings resolved from <c>kyberdash-api</c> arguments and environment variables.
/// </summary>
/// <remarks>
/// Only the values that bind sockets, choose state, or change what the process prints live here;
/// everything else is <see cref="DashOptions"/>, which the application layer owns. The two are
/// deliberately separate so a presentation host never invents application configuration.
/// </remarks>
public class DashRuntimeSettings
{
    /// <summary>Port the web/REST/SPA listener binds; <c>0</c> asks the OS for an ephemeral port.</summary>
    public int WebPort { get; set; } = 4747;

    /// <summary>Whether an API port was chosen explicitly rather than read from settings or the default.</summary>
    public bool WebPortExplicit { get; set; }

    /// <summary>Port the OTLP collector binds before any fallback is applied.</summary>
    public int OtlpPort { get; set; } = 4318;

    /// <summary>
    /// Whether <c>--otlp-port</c> named the port. An explicitly requested port never silently falls
    /// back to an ephemeral one: a collector that quietly moves is worse than one that refuses.
    /// </summary>
    public bool OtlpPortExplicit { get; set; }

    /// <summary>Whether the OTLP collector should be started at all.</summary>
    public bool OtlpEnabled { get; set; } = true;

    /// <summary>Whether only the OTLP collector starts, with no web/REST listener.</summary>
    public bool OtlpOnly { get; set; }

    /// <summary>Whether the dashboard URL opens in the operator's browser once listening.</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>Optional SPA path opened in the browser, validated against the known view patterns.</summary>
    public string? View { get; set; }

    /// <summary>Optional override directory holding the built SPA assets.</summary>
    public string? DashboardDirectory { get; set; }

    /// <summary>State directory holding the engine lease, <c>server.json</c>, and the dashboard settings.</summary>
    public string StateDirectory { get; set; } = string.Empty;

    /// <summary>Resolved canonical SQLite store path, or <see langword="null"/> to let the engine decide.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Reported engine version.</summary>
    public string Version { get; set; } = "0.21.0";

    /// <summary>Maximum accepted OTLP request body in bytes.</summary>
    public long MaxOtlpBodyBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Collector port capture status may name. A disabled collector, or a port the process has not
    /// bound, is omitted so the status does not advertise a listener that is not there.
    /// </summary>
    /// <returns>The bound collector port, or <see langword="null"/> when there is nothing to name.</returns>
    public int? CaptureStatusOtlpPort() => OtlpEnabled && OtlpPort > 0 ? OtlpPort : null;

    /// <summary>Builds the application-layer options passed to <c>AddDashApplication</c>.</summary>
    public DashOptions ToDashOptions()
    {
        return new DashOptions
        {
            DatabasePath = DatabasePath,
            StateDirectory = StateDirectory,
            DashboardDir = DashboardDirectory,
            Version = Version,
            MaxOtlpBodyBytes = MaxOtlpBodyBytes,
            OtlpPort = OtlpPort,
        };
    }

    /// <summary>Keeps the runtime copy in step with options an application layer may have adjusted.</summary>
    public void ApplyDashOptions(DashOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        DatabasePath = options.DatabasePath ?? DatabasePath;
        StateDirectory = options.StateDirectory ?? StateDirectory;
        MaxOtlpBodyBytes = options.MaxOtlpBodyBytes > 0 ? options.MaxOtlpBodyBytes : MaxOtlpBodyBytes;
    }

    /// <summary>Formats the loopback base URL a listener bound on <paramref name="port"/> answers on.</summary>
    public static Uri LoopbackUrl(int port) =>
        new(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}"));
}
