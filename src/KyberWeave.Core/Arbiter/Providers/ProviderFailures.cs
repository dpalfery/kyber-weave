using System.Text.Json;

namespace KyberWeave.Core.Arbiter.Providers;

/// <summary>
/// The provider failures a fail-safe answer absorbs. A transport, timeout or response-decoding failure means the
/// provider answered nothing usable, so the caller keeps its safe default and records why. Any other exception is a
/// bug and propagates.
/// </summary>
public static class ProviderFailures
{
    /// <summary>Whether <paramref name="exception"/> is a provider failure a fail-safe answer absorbs.</summary>
    /// <param name="exception">The exception the provider call raised.</param>
    /// <returns>True for a transport, timeout, decoding or unreachable-state failure.</returns>
    public static bool IsAbsorbable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is HttpRequestException
            or IOException
            or TimeoutException
            or JsonException
            or InvalidOperationException
            or UnauthorizedAccessException;
    }
}
