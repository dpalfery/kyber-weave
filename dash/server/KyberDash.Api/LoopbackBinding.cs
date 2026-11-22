using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;

namespace KyberDash.Api;

/// <summary>
/// Pure decisions for which loopback address is the dashboard, which bind failure to report,
/// and which routes belong on which port.
/// </summary>
public static class LoopbackBinding
{
    /// <summary>
    /// Picks the dashboard URL from addresses Kestrel actually bound. A configured web port wins
    /// over an earlier collector address; an ephemeral web port (<c>0</c>) skips the collector port.
    /// </summary>
    /// <param name="addresses">Bound listener URLs.</param>
    /// <param name="configuredWebPort">The web port the operator asked for, or <c>0</c> for ephemeral.</param>
    /// <param name="otlpPort">The collector port, used so an ephemeral web bind is not reported as the collector.</param>
    /// <returns>The loopback dashboard URL.</returns>
    public static Uri SelectWebUrl(IEnumerable<string> addresses, int configuredWebPort, int otlpPort)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        Uri? ephemeralWeb = null;
        foreach (string address in addresses)
        {
            if (!TryLoopback(address, out Uri? uri))
                continue;

            if (configuredWebPort > 0 && uri.Port == configuredWebPort)
                return DashRuntimeSettings.LoopbackUrl(uri.Port);

            if (configuredWebPort == 0 && uri.Port != otlpPort)
                ephemeralWeb ??= DashRuntimeSettings.LoopbackUrl(uri.Port);
        }

        if (ephemeralWeb is not null)
            return ephemeralWeb;

        return DashRuntimeSettings.LoopbackUrl(configuredWebPort);
    }

    /// <summary>
    /// Picks the collector port from addresses Kestrel actually bound.
    /// </summary>
    /// <param name="addresses">Bound listener URLs.</param>
    /// <param name="configuredOtlpPort">The collector port before bind, or <c>0</c> when the OS chose one.</param>
    /// <param name="webPort">The dashboard port, skipped when the collector port was ephemeral and the two differ.</param>
    /// <returns>The bound collector port, or <c>0</c> when a configured port is not among the addresses.</returns>
    public static int SelectOtlpPort(IEnumerable<string> addresses, int configuredOtlpPort, int webPort)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        List<int> ports = [];
        foreach (string address in addresses)
        {
            if (TryLoopback(address, out Uri? uri))
                ports.Add(uri.Port);
        }

        if (configuredOtlpPort > 0)
        {
            foreach (int port in ports)
            {
                if (port == configuredOtlpPort)
                    return port;
            }

            // A miss must not fall through to the web listener. The caller treats a positive
            // result as the port this process bound.
            return 0;
        }

        if (configuredOtlpPort == 0)
        {
            foreach (int port in ports)
            {
                if (webPort <= 0 || port != webPort || ports.Count == 1)
                    return port;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether a bind failure should be retried on an ephemeral collector port. An explicit
    /// <c>--otlp-port</c> never moves.
    /// </summary>
    /// <param name="otlpEnabled">Whether the collector is started.</param>
    /// <param name="otlpPortExplicit">Whether the operator named the collector port.</param>
    /// <param name="preferredPortFree">Whether the preferred collector port accepted a bind.</param>
    /// <returns><see langword="true"/> when the host should bind port <c>0</c> instead.</returns>
    public static bool ShouldUseEphemeralOtlpPort(bool otlpEnabled, bool otlpPortExplicit, bool preferredPortFree) =>
        otlpEnabled && !otlpPortExplicit && !preferredPortFree;

    /// <summary>
    /// Walks <paramref name="exception"/> for a socket bind failure, including Kestrel's
    /// <see cref="AddressInUseException"/> wrapped around <see cref="SocketException"/>.
    /// </summary>
    /// <param name="exception">The exception <c>StartAsync</c> threw.</param>
    /// <returns><see langword="true"/> when the process failed to bind a listener.</returns>
    public static bool IsBindFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException or AddressInUseException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Names the address that failed to bind. The message Kestrel throws already carries that URL;
    /// the web port is not a substitute for it.
    /// </summary>
    /// <param name="exception">The bind failure.</param>
    /// <param name="runtime">Transport settings, used only when the exception names no URL.</param>
    /// <returns>One stderr line for the operator.</returns>
    public static string DescribeBindFailure(Exception exception, DashRuntimeSettings runtime)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(runtime);

        string target = ExtractUrl(exception) ?? FallbackUrl(runtime);
        string detail = Innermost(exception).Message;
        return string.Create(CultureInfo.InvariantCulture, $"kyberdash-api: could not listen on {target}: {detail}");
    }

    /// <summary>
    /// Whether <paramref name="path"/> may be served on <paramref name="localPort"/>. Dashboard routes
    /// stay on the web port and collector routes stay on the OTLP port when the two differ. An
    /// unresolved ephemeral port, or a port that is neither, is left open so a test host is not locked out.
    /// </summary>
    /// <param name="path">Request path.</param>
    /// <param name="localPort">The socket port that accepted the request.</param>
    /// <param name="runtime">Transport settings.</param>
    /// <returns><see langword="false"/> when the path is on the wrong listener.</returns>
    public static bool RequestAllowed(string path, int localPort, DashRuntimeSettings runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        bool otlpPath = IsOtlpPath(path);
        if (runtime.OtlpOnly)
            return otlpPath;

        if (!runtime.OtlpEnabled || runtime.WebPort <= 0 || runtime.OtlpPort <= 0 || runtime.WebPort == runtime.OtlpPort)
            return true;

        if (localPort != runtime.WebPort && localPort != runtime.OtlpPort)
            return true;

        return localPort == runtime.OtlpPort ? otlpPath : !otlpPath;
    }

    /// <summary>Whether <paramref name="path"/> is the collector surface (<c>/healthz</c> or <c>/v1/...</c>).</summary>
    /// <param name="path">Request path.</param>
    /// <returns><see langword="true"/> for collector routes.</returns>
    public static bool IsOtlpPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        if (string.Equals(path, "/healthz", StringComparison.OrdinalIgnoreCase))
            return true;

        return path.Equals("/v1", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tries to bind <paramref name="port"/> on loopback. Port <c>0</c> is treated as free because the
    /// OS chooses it.
    /// </summary>
    /// <param name="port">The port to probe.</param>
    /// <returns><see langword="false"/> when the port is already taken.</returns>
    public static bool LoopbackPortIsFree(int port)
    {
        if (port == 0)
            return true;

        try
        {
            using Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool TryLoopback(string address, out Uri uri)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out Uri? parsed) && parsed.Port > 0)
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static string? ExtractUrl(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            int start = message.IndexOf("http://", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                start = message.IndexOf("https://", StringComparison.OrdinalIgnoreCase);

            if (start < 0)
                continue;

            int end = start;
            while (end < message.Length && !char.IsWhiteSpace(message[end]))
                end++;

            // Kestrel writes "Failed to bind to address http://127.0.0.1:4318: address already in use."
            return message[start..end].TrimEnd(':');
        }

        return null;
    }

    private static string FallbackUrl(DashRuntimeSettings runtime)
    {
        int port = runtime.OtlpEnabled && runtime.OtlpPort != runtime.WebPort ? runtime.OtlpPort : runtime.WebPort;
        return DashRuntimeSettings.LoopbackUrl(port).GetLeftPart(UriPartial.Authority);
    }

    private static Exception Innermost(Exception exception)
    {
        Exception current = exception;
        while (current.InnerException is not null)
            current = current.InnerException;

        return current;
    }
}
