using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyberWeave.Cli.Update;

/// <summary>Reads GitHub Release metadata and assets over HTTPS only.</summary>
internal sealed class GitHubReleaseClient : IDisposable
{
    internal const string Owner = "dpalfery";
    internal const string Repo = "kyber-weave";

    internal static readonly Uri LatestApi =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    internal static readonly Uri ReleasesApi =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases");

    internal static readonly Uri ReleaseDownloadBase =
        new($"https://github.com/{Owner}/{Repo}/releases/download/");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    private readonly HttpClient _client;

    internal GitHubReleaseClient(
        HttpMessageHandler handler,
        string userAgentVersion,
        Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(readEnvironment);

        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("kyber-weave", ProductVersion(userAgentVersion)));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        string? token = readEnvironment("GITHUB_TOKEN") ?? readEnvironment("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    internal string ResolveLatestStable()
    {
        string json = GetString(LatestApi, "the latest stable release");
        GitHubRelease payload;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions)
                ?? throw new SelfUpdateException("GitHub latest-release response was empty.");
        }
        catch (JsonException ex)
        {
            throw new SelfUpdateException("could not parse the latest GitHub Release response.", ex);
        }

        if (string.IsNullOrWhiteSpace(payload.TagName))
            throw new SelfUpdateException("could not resolve the latest release. Pass a version explicitly.");

        return ReleaseVersion.Normalize(payload.TagName);
    }

    internal string ResolveNewestListed()
    {
        string json = GetString(ReleasesApi, "the GitHub Releases list");
        GitHubRelease[] payload;
        try
        {
            payload = JsonSerializer.Deserialize<GitHubRelease[]>(json, JsonOptions)
                ?? throw new SelfUpdateException("GitHub Releases list was empty.");
        }
        catch (JsonException ex)
        {
            throw new SelfUpdateException("could not parse the GitHub Releases list.", ex);
        }

        foreach (GitHubRelease release in payload)
        {
            if (release.Draft || string.IsNullOrWhiteSpace(release.TagName))
                continue;

            return ReleaseVersion.Normalize(release.TagName);
        }

        throw new SelfUpdateException("could not resolve the latest release. Pass a version explicitly.");
    }

    internal string DownloadChecksums(string tag)
    {
        Uri uri = AssetUri(tag, "SHA256SUMS.txt");
        return GetString(uri, $"SHA256SUMS.txt for {tag}");
    }

    internal void DownloadAsset(string tag, string archiveName, string destinationPath)
    {
        Uri uri = AssetUri(tag, archiveName);
        using HttpResponseMessage response = Send(uri);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SelfUpdateException(
                $"could not download {archiveName} for {tag}. Does that release exist?");
        }

        EnsureSuccess(response, $"download failed: {uri}");
        using FileStream destination = File.Create(destinationPath);
        using Stream stream = response.Content.ReadAsStream();
        stream.CopyTo(destination);
    }

    public void Dispose() => _client.Dispose();

    internal static void EnsureHttps(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new SelfUpdateException($"refusing non-HTTPS URL: {uri}");
    }

    internal static Uri AssetUri(string tag, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains('\\', StringComparison.Ordinal))
            throw new SelfUpdateException($"refusing asset name '{fileName}'.");

        Uri uri = new Uri(ReleaseDownloadBase, $"{tag}/{fileName}");
        EnsureHttps(uri);
        return uri;
    }

    private string GetString(Uri uri, string what)
    {
        using HttpResponseMessage response = Send(uri);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new SelfUpdateException($"could not download {what}. Does that release exist?");

        EnsureSuccess(response, $"could not download {what}");
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Disposing the request before the response is fully read closes the download stream.")]
    private HttpResponseMessage Send(Uri uri)
    {
        EnsureHttps(uri);
        return _client
            .SendAsync(
                new HttpRequestMessage(HttpMethod.Get, uri),
                HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();
    }

    private static void EnsureSuccess(HttpResponseMessage response, string failure)
    {
        if (response.IsSuccessStatusCode)
            return;

        int status = (int)response.StatusCode;
        throw new SelfUpdateException($"{failure} (HTTP {status}).");
    }

    private static string ProductVersion(string version)
    {
        try
        {
            string normalized = ReleaseVersion.Normalize(version);
            foreach (char c in normalized)
            {
                if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
                    continue;
                return "0.0.0";
            }

            return normalized;
        }
        catch (SelfUpdateException)
        {
            return "0.0.0";
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
    }
}
