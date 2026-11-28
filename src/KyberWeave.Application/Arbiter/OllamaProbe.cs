using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Core.Arbiter;

/// <summary>Probes an Ollama <c>/api/version</c> URL over the registered HTTP transport.</summary>
/// <param name="handlers">Creates the HTTP transport.</param>
public sealed class OllamaProbe(IArbiterHttpHandlers handlers) : IOllamaProbe
{
    /// <inheritdoc />
    public bool TryDetect(Uri versionUri, out string? version)
    {
        version = null;
        ArgumentNullException.ThrowIfNull(versionUri);
        if (versionUri.Scheme != Uri.UriSchemeHttp && versionUri.Scheme != Uri.UriSchemeHttps)
            return false;

        try
        {
            using HttpClient client = new(handlers.Create(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            using HttpResponseMessage response = client.GetAsync(versionUri).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return false;

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("version", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? found = versionElement.GetString();
            if (!ArbiterCliText.IsOllamaSupported(found))
                return false;

            version = found;
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or TimeoutException
            or JsonException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<OllamaProbeResult> TryDetectAsync(Uri versionUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(versionUri);
        if (versionUri.Scheme != Uri.UriSchemeHttp && versionUri.Scheme != Uri.UriSchemeHttps)
            return new OllamaProbeResult(false, null);

        try
        {
            using HttpClient client = new(handlers.Create(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            using HttpResponseMessage response = await client.GetAsync(versionUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new OllamaProbeResult(false, null);

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("version", out JsonElement versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                return new OllamaProbeResult(false, null);
            }

            string? found = versionElement.GetString();
            if (!ArbiterCliText.IsOllamaSupported(found))
                return new OllamaProbeResult(false, null);

            return new OllamaProbeResult(true, found);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or TaskCanceledException
            or TimeoutException
            or JsonException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            return new OllamaProbeResult(false, null);
        }
    }
}
