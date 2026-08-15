using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KyberWeave.Core.Squad.Release;

namespace KyberWeave.Cli.Commands.Squad.Infrastructure;

/// <summary>Retrieves a version-matched Squad asset from a GitHub release.</summary>
/// <remarks>
/// Release bytes are staged only after the HTTPS chain and exact checksum row have
/// been verified. Archive validation completes before the destination is created,
/// which keeps failures outside the deployment transaction side-effect free.
/// </remarks>
public sealed partial class GitHubSquadReleaseSource : ISquadReleaseSource
{
    private const string ChecksumAssetName = "SHA256SUMS.txt";
    private const int MaximumRedirects = 10;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly Uri _apiRoot;
    private readonly Action? _onStagingCreated;

    /// <summary>Creates a source with a transport that exposes every redirect for validation.</summary>
    public GitHubSquadReleaseSource(Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(apiRoot);

        _handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        _httpClient = new HttpClient(_handler, disposeHandler: false);
        _apiRoot = apiRoot;
        _onStagingCreated = null;
    }

    /// <summary>Creates a source over an injected handler for deterministic hosting and tests.</summary>
    internal GitHubSquadReleaseSource(
        HttpMessageHandler handler,
        Uri apiRoot,
        Action? onStagingCreated = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(apiRoot);

        if (handler is HttpClientHandler { AllowAutoRedirect: true } or
            SocketsHttpHandler { AllowAutoRedirect: true })
        {
            throw new ArgumentException(
                "The release transport must disable automatic redirects so every redirect can be inspected.",
                nameof(handler));
        }

        _handler = handler;
        _httpClient = new HttpClient(_handler, disposeHandler: false);
        _apiRoot = apiRoot;
        _onStagingCreated = onStagingCreated;
    }

    /// <inheritdoc />
    public async Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateHttps(_apiRoot, "GitHub API origin");
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string extractionRoot = Path.GetFullPath(request.DestinationPath);
        RejectExistingSymbolicLinks(extractionRoot);

        Uri releaseUri = BuildReleaseUri(request.Repository, request.Version);
        GitHubRelease release = await ReadReleaseAsync(releaseUri, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(release.TagName, $"v{request.Version}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"GitHub returned release tag '{release.TagName}' instead of 'v{request.Version}'.");
        }

        string archiveName = $"kyber-squad-{request.Version}.zip";
        SquadReleaseAsset archive = SelectAsset(release.Assets, archiveName);
        SquadReleaseAsset checksumAsset = SelectAsset(release.Assets, ChecksumAssetName);

        byte[] checksumBytes = await DownloadBytesAsync(
            checksumAsset.DownloadUri,
            cancellationToken).ConfigureAwait(false);
        SquadReleaseChecksum checksum = ParseChecksum(checksumBytes, archiveName);

        byte[] archiveBytes = await DownloadBytesAsync(
            archive.DownloadUri,
            cancellationToken).ConfigureAwait(false);
        string actualDigest = Convert.ToHexStringLower(SHA256.HashData(archiveBytes));
        if (!string.Equals(actualDigest, checksum.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The checksum for release asset '{archiveName}' does not match SHA256SUMS.txt.");
        }

        IReadOnlyList<ValidatedArchiveEntry> entries = ValidateArchive(archiveBytes, extractionRoot, cancellationToken);
        await ExtractAtomicallyAsync(
            archiveBytes,
            extractionRoot,
            entries,
            _onStagingCreated,
            cancellationToken).ConfigureAwait(false);

        return new SquadReleaseResult(
            request.Version,
            archive,
            checksum,
            extractionRoot,
            entries.Where(entry => !entry.IsDirectory)
                .Select(entry => entry.RelativePath)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    private static void ValidateRequest(SquadReleaseRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);

        string[] repositoryParts = request.Repository.Split('/');
        if (repositoryParts.Length != 2 || repositoryParts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "The release repository must use exact 'owner/name' form.",
                nameof(request));
        }

        if (!IsStableVersion(request.Version))
        {
            throw new ArgumentException(
                "The release version must use stable X.Y.Z form with no leading zeros.",
                nameof(request));
        }
    }

    private static bool IsStableVersion(string version)
    {
        string[] parts = version.Split('.', StringSplitOptions.None);
        return parts.Length == 3 && parts.All(part =>
            part.Length > 0 &&
            (part.Length == 1 || part[0] != '0') &&
            part.All(char.IsAsciiDigit));
    }

    private Uri BuildReleaseUri(string repository, string version)
    {
        string[] parts = repository.Split('/');
        string relative = $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}" +
            $"/releases/tags/v{Uri.EscapeDataString(version)}";
        Uri releaseUri = new Uri(_apiRoot, relative);
        ValidateHttps(releaseUri, "GitHub release URI");
        return releaseUri;
    }

    private async Task<GitHubRelease> ReadReleaseAsync(
        Uri releaseUri,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendHttpsAsync(releaseUri, cancellationToken).ConfigureAwait(false);
        GitHubRelease? release = await JsonSerializer.DeserializeAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            ReleaseJsonContext.Default.GitHubRelease,
            cancellationToken).ConfigureAwait(false);

        return release ?? throw new InvalidDataException("The GitHub release response was empty.");
    }

    private async Task<byte[]> DownloadBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateHttps(uri, "GitHub release asset URI");
        using HttpResponseMessage response = await SendHttpsAsync(uri, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendHttpsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            ValidateHttps(currentUri, "HTTP request or redirect");
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("kyber-weave");
            HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                if (!response.IsSuccessStatusCode)
                {
                    try
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    finally
                    {
                        response.Dispose();
                    }
                }

                return response;
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new InvalidDataException("An HTTPS release redirect omitted its Location header.");

            currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            ValidateHttps(currentUri, "HTTP redirect");
        }

        throw new InvalidOperationException(
            $"The release request exceeded the {MaximumRedirects} redirect limit.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static void ValidateHttps(Uri uri, string subject)
    {
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            throw new InvalidOperationException($"{subject} must use HTTPS.");
    }

    private static SquadReleaseAsset SelectAsset(
        IReadOnlyList<GitHubReleaseAsset> assets,
        string assetName)
    {
        GitHubReleaseAsset[] matches = assets.Where(
            asset => string.Equals(asset.Name, assetName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            string qualifier = matches.Length == 0 ? "does not contain" : "contains duplicate";
            throw new InvalidDataException(
                $"The GitHub release {qualifier} exact asset '{assetName}'.");
        }

        if (!Uri.TryCreate(matches[0].BrowserDownloadUrl, UriKind.Absolute, out Uri? downloadUri))
            throw new InvalidDataException($"Release asset '{assetName}' has an invalid download URI.");

        ValidateHttps(downloadUri, $"Release asset '{assetName}' URI");
        return new SquadReleaseAsset(assetName, downloadUri, matches[0].Size);
    }

    private static SquadReleaseChecksum ParseChecksum(byte[] manifestBytes, string assetName)
    {
        string manifest;
        try
        {
            manifest = StrictUtf8.GetString(manifestBytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The release checksum manifest is not valid UTF-8.", exception);
        }

        string[] rows = manifest.Split('\n')
            .Select(line => line.EndsWith('\r') ? line[..^1] : line)
            .ToArray();
        string[] candidates = rows.Where(line => HasExactAssetName(line, assetName)).ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidDataException(
                $"The release checksum manifest has no exact checksum row for '{assetName}'.");
        }

        if (candidates.Length > 1)
        {
            throw new InvalidDataException(
                $"The release checksum manifest has duplicate checksum rows for '{assetName}'.");
        }

        string suffix = $"  {assetName}";
        string exactRow = candidates[0];
        if (exactRow.Length != 64 + suffix.Length ||
            !exactRow.EndsWith(suffix, StringComparison.Ordinal) ||
            !IsSha256(exactRow.AsSpan(0, 64)))
        {
            throw new InvalidDataException(
                $"The release checksum manifest has an invalid checksum row for '{assetName}'.");
        }

        return new SquadReleaseChecksum(
            assetName,
            exactRow[..64].ToLowerInvariant());
    }

    private static bool HasExactAssetName(string line, string assetName)
    {
        if (!line.EndsWith(assetName, StringComparison.Ordinal))
            return false;

        int assetNameStart = line.Length - assetName.Length;
        return assetNameStart > 0 &&
            (char.IsWhiteSpace(line[assetNameStart - 1]) || line[assetNameStart - 1] == '*');
    }

    private static bool IsSha256(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
                return false;
        }

        return true;
    }

    private static IReadOnlyList<ValidatedArchiveEntry> ValidateArchive(
        byte[] archiveBytes,
        string extractionRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using MemoryStream stream = new MemoryStream(archiveBytes, writable: false);
            using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            StringComparer comparison = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            HashSet<string> seen = new HashSet<string>(comparison);
            HashSet<string> files = new HashSet<string>(comparison);
            HashSet<string> requiredDirectories = new HashSet<string>(comparison);
            List<ValidatedArchiveEntry> entries = new List<ValidatedArchiveEntry>(archive.Entries.Count);

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsSymbolicLink(entry))
                {
                    throw new InvalidDataException(
                        $"The Squad archive contains a symbolic link entry: '{entry.FullName}'.");
                }

                ValidatedArchiveEntry validated = ValidateArchivePath(entry.FullName, extractionRoot);
                if (!seen.Add(validated.RelativePath))
                {
                    throw new InvalidDataException(
                        $"The Squad archive contains duplicate path '{validated.RelativePath}'.");
                }

                ValidateArchiveHierarchy(validated, files, requiredDirectories);

                entries.Add(validated);
            }

            return entries;
        }
        catch (InvalidDataException exception) when (!exception.Message.Contains(
            "Squad archive",
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Squad archive is invalid or unreadable.", exception);
        }
    }

    private static ValidatedArchiveEntry ValidateArchivePath(string entryName, string extractionRoot)
    {
        if (string.IsNullOrEmpty(entryName) ||
            entryName.Contains('\0', StringComparison.Ordinal) ||
            entryName.StartsWith('/') ||
            entryName.StartsWith('\\') ||
            (entryName.Length >= 2 && char.IsAsciiLetter(entryName[0]) && entryName[1] == ':'))
        {
            throw new InvalidDataException(
                $"The Squad archive contains an invalid rooted path: '{entryName}'.");
        }

        string portablePath = entryName.Replace('\\', '/');
        bool isDirectory = portablePath.EndsWith('/');
        string[] segments = portablePath.Split('/', StringSplitOptions.None);
        int segmentCount = isDirectory ? segments.Length - 1 : segments.Length;
        if (segmentCount == 0 || segments.Take(segmentCount).Any(
            segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"The Squad archive contains an unsafe path: '{entryName}'.");
        }

        string relativePath = string.Join('/', segments.Take(segmentCount));
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(extractionRoot, platformPath));
        string containmentPrefix = extractionRoot.EndsWith(Path.DirectorySeparatorChar)
            ? extractionRoot
            : extractionRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(containmentPrefix, comparison))
        {
            throw new InvalidDataException(
                $"The Squad archive path escapes its extraction root: '{entryName}'.");
        }

        return new ValidatedArchiveEntry(relativePath, isDirectory);
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const uint UnixFileTypeMask = 0xF000;
        const uint UnixSymbolicLink = 0xA000;
        uint unixMode = ((uint)entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        bool windowsReparsePoint =
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
        return unixMode == UnixSymbolicLink || windowsReparsePoint;
    }

    private static void ValidateArchiveHierarchy(
        ValidatedArchiveEntry entry,
        ISet<string> files,
        ISet<string> requiredDirectories)
    {
        string[] segments = entry.RelativePath.Split('/');
        for (int index = 1; index < segments.Length; index++)
        {
            string ancestor = string.Join('/', segments.Take(index));
            if (files.Contains(ancestor))
            {
                throw new InvalidDataException(
                    $"The Squad archive path '{entry.RelativePath}' descends through file '{ancestor}'.");
            }

            requiredDirectories.Add(ancestor);
        }

        if (entry.IsDirectory)
        {
            requiredDirectories.Add(entry.RelativePath);
            return;
        }

        if (requiredDirectories.Contains(entry.RelativePath))
        {
            throw new InvalidDataException(
                $"The Squad archive path '{entry.RelativePath}' conflicts with a directory.");
        }

        files.Add(entry.RelativePath);
    }

    private static async Task ExtractAtomicallyAsync(
        byte[] archiveBytes,
        string extractionRoot,
        IReadOnlyList<ValidatedArchiveEntry> entries,
        Action? onStagingCreated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(extractionRoot) || File.Exists(extractionRoot))
        {
            throw new IOException(
                $"Squad extraction destination already exists: '{extractionRoot}'.");
        }

        string? parent = Path.GetDirectoryName(extractionRoot);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException(
                "The parent directory for the Squad extraction destination must already exist.");
        }

        string stagingRoot = Path.Combine(
            parent,
            $".{Path.GetFileName(extractionRoot)}.kyber-squad-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingRoot);
            onStagingCreated?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            using MemoryStream stream = new MemoryStream(archiveBytes, writable: false);
            using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            for (int index = 0; index < archive.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ZipArchiveEntry entry = archive.Entries[index];
                ValidatedArchiveEntry validated = entries[index];
                string destination = Path.Combine(
                    stagingRoot,
                    validated.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (validated.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                string destinationDirectory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException("The Squad archive contains an invalid file path.");
                Directory.CreateDirectory(destinationDirectory);
                await using Stream source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream target = new FileStream(destination, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingRoot, extractionRoot);
        }
        catch
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
            throw;
        }
    }

    private static void RejectExistingSymbolicLinks(string extractionRoot)
    {
        // Platform temporary roots can themselves be links (for example /var on macOS).
        // The release port owns only the requested destination tree; containment above
        // that boundary belongs to the caller's deployment-root policy.
        if (new FileInfo(extractionRoot).LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Squad extraction rejects an existing symbolic link: '{extractionRoot}'.");
        }

        if (!Path.Exists(extractionRoot))
            return;

        RejectSymbolicLink(extractionRoot);
        if (Directory.Exists(extractionRoot))
            RejectSymbolicLinksBelow(extractionRoot);
    }

    private static void RejectSymbolicLinksBelow(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(
            directory,
            "*",
            SearchOption.TopDirectoryOnly))
        {
            RejectSymbolicLink(entry);
            if (Directory.Exists(entry))
                RejectSymbolicLinksBelow(entry);
        }
    }

    private static void RejectSymbolicLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Squad extraction rejects an existing symbolic link: '{path}'.");
        }
    }

    private sealed record ValidatedArchiveEntry(string RelativePath, bool IsDirectory);

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public required string TagName { get; init; }

        [JsonPropertyName("assets")]
        public required IReadOnlyList<GitHubReleaseAsset> Assets { get; init; }
    }

    private sealed record GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public required string BrowserDownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }
    }

    [JsonSerializable(typeof(GitHubRelease))]
    private sealed partial class ReleaseJsonContext : JsonSerializerContext;
}
