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
    /// <returns>
    /// True for a transport, timeout, decoding or access failure. <see cref="TaskCanceledException"/> counts: an
    /// <c>HttpClient</c> timeout raises it, so it is absorbed here. Callers must still let a cancellation of their own
    /// token propagate, which they check before consulting this method.
    /// </returns>
    /// <remarks>
    /// <see cref="InvalidOperationException"/> is not absorbed. It is how a programming bug surfaces (a collection
    /// modified during enumeration, a call in the wrong state), and absorbing it would hide the bug behind a safe answer.
    /// </remarks>
    public static bool IsAbsorbable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is HttpRequestException
            or IOException
            or TimeoutException
            or TaskCanceledException
            or JsonException
            or UnauthorizedAccessException;
    }
}
