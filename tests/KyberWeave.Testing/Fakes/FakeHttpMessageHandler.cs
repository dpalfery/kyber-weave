using System.Net;
using System.Net.Http.Headers;

namespace KyberWeave.Tests.Fakes;

/// <summary>One request a <see cref="FakeHttpMessageHandler"/> saw, with its body read at send time.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The request address.</param>
/// <param name="Body">The request body, or an empty string when there was none.</param>
/// <param name="ContentType">The body's media type, or null when there was no body.</param>
/// <param name="Authorization">The authorization header, or null.</param>
/// <param name="UserAgent">The user-agent header, as sent.</param>
/// <param name="Accept">The accept header, as sent.</param>
/// <param name="CanBeCanceled">Whether the call carried a token that can cancel it.</param>
public sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri? Uri,
    string Body,
    string? ContentType,
    AuthenticationHeaderValue? Authorization,
    string UserAgent,
    string Accept,
    bool CanBeCanceled);

/// <summary>
/// The network seam's fake: an <see cref="HttpMessageHandler"/> that answers from a script and records every request.
/// Nothing leaves the process.
/// </summary>
/// <remarks>
/// A request body is read when the request arrives, because the client disposes its content once the call returns.
/// A call the script does not answer fails the test rather than reaching the network.
/// </remarks>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedHttpRequest, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<RecordedHttpRequest> _requests = [];

    /// <summary>Creates a handler that answers each request with the next response, in order.</summary>
    /// <param name="responses">The responses; the handler disposes none of them.</param>
    public FakeHttpMessageHandler(params HttpResponseMessage[] responses)
    {
        Queue<HttpResponseMessage> queue = new(responses);
        _respond = (_, _) => queue.Count == 0
            ? throw new Xunit.Sdk.XunitException("The client sent an unexpected request.")
            : Task.FromResult(queue.Dequeue());
    }

    /// <summary>Creates a handler that answers each request with the result of <paramref name="respond"/>.</summary>
    /// <param name="respond">Builds the response for a request.</param>
    public FakeHttpMessageHandler(Func<RecordedHttpRequest, HttpResponseMessage> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        _respond = (request, _) => Task.FromResult(respond(request));
    }

    private FakeHttpMessageHandler(Func<RecordedHttpRequest, CancellationToken, Task<HttpResponseMessage>> respond) =>
        _respond = respond;

    /// <summary>Every request the code under test sent, in order.</summary>
    public IReadOnlyList<RecordedHttpRequest> Requests => _requests;

    /// <summary>Creates a handler that never answers: each call waits until its token is cancelled.</summary>
    /// <returns>A handler that models a server that does not reply.</returns>
    public static FakeHttpMessageHandler Hanging() =>
        new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A hanging call returned.");
        });

    /// <summary>Creates a handler whose every call fails the way an unreachable server does.</summary>
    /// <param name="failure">The failure to throw.</param>
    /// <returns>A handler that throws <paramref name="failure"/>.</returns>
    public static FakeHttpMessageHandler Failing(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new((_, _) => throw failure);
    }

    /// <summary>Builds a response with a status and a text body.</summary>
    /// <param name="status">The status code.</param>
    /// <param name="body">The body text.</param>
    /// <param name="mediaType">The body's media type.</param>
    /// <returns>A response the caller owns.</returns>
    public static HttpResponseMessage Response(HttpStatusCode status, string body = "", string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) };

    /// <inheritdoc />
    /// <remarks>
    /// Serves the synchronous <see cref="HttpClient.Send(HttpRequestMessage)"/> the sync twins still use until their
    /// callers move to the async path; it answers exactly as <see cref="SendAsync"/> does.
    /// </remarks>
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) =>
        SendAsync(request, cancellationToken).GetAwaiter().GetResult();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        RecordedHttpRequest recorded = new(
            request.Method,
            request.RequestUri,
            body,
            request.Content?.Headers.ContentType?.MediaType,
            request.Headers.Authorization,
            request.Headers.UserAgent.ToString(),
            request.Headers.Accept.ToString(),
            cancellationToken.CanBeCanceled);
        _requests.Add(recorded);
        return await _respond(recorded, cancellationToken).ConfigureAwait(false);
    }
}
