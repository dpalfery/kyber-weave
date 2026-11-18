using System.Net;
using System.Text;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Testing.Update;

/// <summary>
/// The release transport in a unit test: a handler that answers each release address from a table over the network
/// seam's fake, and answers 404 for an address nobody mapped. Nothing leaves the process.
/// </summary>
public sealed class ReleaseMapHandler : DelegatingHandler
{
    private readonly Dictionary<string, byte[]> _bodies;
    private readonly Dictionary<string, string> _mediaTypes;
    private readonly FakeHttpMessageHandler _network;

    /// <summary>Creates a handler with nothing mapped.</summary>
    public ReleaseMapHandler()
        : this(new Dictionary<string, byte[]>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal))
    {
    }

    private ReleaseMapHandler(Dictionary<string, byte[]> bodies, Dictionary<string, string> mediaTypes)
        : this(bodies, mediaTypes, new FakeHttpMessageHandler(request => Answer(bodies, mediaTypes, request)))
    {
    }

    private ReleaseMapHandler(
        Dictionary<string, byte[]> bodies,
        Dictionary<string, string> mediaTypes,
        FakeHttpMessageHandler network)
        : base(network)
    {
        _bodies = bodies;
        _mediaTypes = mediaTypes;
        _network = network;
    }

    /// <summary>Gets every address requested, in order.</summary>
    public IReadOnlyList<string> Uris => [.. _network.Requests.Select(request => request.Uri?.AbsoluteUri ?? string.Empty)];

    /// <summary>Gets the authorization header of every request, in order; null where there was none.</summary>
    public IReadOnlyList<string?> Authorizations => [.. _network.Requests.Select(request => request.Authorization?.ToString())];

    /// <summary>Maps an address to a JSON body.</summary>
    /// <param name="uri">The absolute address.</param>
    /// <param name="json">The body.</param>
    public void MapJson(Uri uri, string json)
    {
        string key = uri.AbsoluteUri;
        _bodies[key] = Encoding.UTF8.GetBytes(json);
        _mediaTypes[key] = "application/json";
    }

    /// <summary>Maps an address to a binary body.</summary>
    /// <param name="uri">The absolute address.</param>
    /// <param name="bytes">The body.</param>
    public void MapFile(Uri uri, byte[] bytes)
    {
        _bodies[uri.AbsoluteUri] = bytes;
        _mediaTypes.Remove(uri.AbsoluteUri);
    }

    /// <summary>Reads what an address is mapped to.</summary>
    /// <param name="uri">The absolute address.</param>
    /// <returns>The body.</returns>
    public byte[] Body(Uri uri) => _bodies[uri.AbsoluteUri];

    private static HttpResponseMessage Answer(
        Dictionary<string, byte[]> bodies,
        Dictionary<string, string> mediaTypes,
        RecordedHttpRequest request)
    {
        string uri = request.Uri?.AbsoluteUri ?? string.Empty;
        if (!bodies.TryGetValue(uri, out byte[]? bytes))
            return FakeHttpMessageHandler.Response(HttpStatusCode.NotFound, "not found", "text/plain");

        ByteArrayContent content = new(bytes);
        if (mediaTypes.TryGetValue(uri, out string? mediaType))
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
