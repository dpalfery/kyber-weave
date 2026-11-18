using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Update;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
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

    /// <summary>The largest release asset one download buffers: the Squad bundle and its checksum list are far smaller.</summary>
    internal const long DefaultMaximumAssetBytes = 512L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly Uri _apiRoot;
    private readonly string? _token;
    private readonly Action? _onStagingCreated;
    private readonly long _maximumAssetBytes;
    private readonly IWritableFileSystem _files;
    private readonly IPathResolver _paths;
    private readonly IProcessEnvironment _environment;
    private readonly IIdGenerator _ids;
    private readonly bool _isWindows;

    // Derived from the root rather than passed separately: a caller can only reach the
    // relaxed transport by having already supplied a loopback API root, which
    // SquadCommandComposition does only for a validated KYBER_WEAVE_RELEASE_ORIGIN.
    private readonly bool _allowLoopbackHttp;

    /// <summary>Creates a source over an injected transport and the file, path, environment and identifier seams.</summary>
    /// <param name="handler">The transport; it must not follow redirects, so every redirect can be inspected. The source disposes it.</param>
    /// <param name="apiRoot">The release API root: github.com, or a validated loopback override.</param>
    /// <param name="files">The file system the archive is staged and published through.</param>
    /// <param name="paths">Resolves the destination to a full path.</param>
    /// <param name="environment">Supplies the GitHub token, the working directory and the operating-system family.</param>
    /// <param name="ids">Names the staging directory.</param>
    public GitHubSquadReleaseSource(
        HttpMessageHandler handler,
        Uri apiRoot,
        IWritableFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IIdGenerator ids)
        : this(handler, apiRoot, files, paths, environment, ids, onStagingCreated: null)
    {
    }

    /// <summary>Creates a source with the test hooks: a staging notification and a lower asset-size limit.</summary>
    internal GitHubSquadReleaseSource(
        HttpMessageHandler handler,
        Uri apiRoot,
        IWritableFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment,
        IIdGenerator ids,
        Action? onStagingCreated,
        long maximumAssetBytes = DefaultMaximumAssetBytes)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(apiRoot);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(ids);

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
        _files = files;
        _paths = paths;
        _environment = environment;
        _ids = ids;
        _isWindows = environment.OperatingSystem == OperatingSystemKind.Windows;
        _token = ReadToken(environment.GetVariable);
        _maximumAssetBytes = maximumAssetBytes;
        _onStagingCreated = onStagingCreated;
        _allowLoopbackHttp = ReleaseOrigin.IsLoopbackAuthority(apiRoot);
    }

    /// <summary>
    /// Reads the GitHub token from the environment, matching
    /// <c>GitHubReleaseClient</c>'s lookup so both release paths
    /// authenticate the same anonymous-limit callers out of.
    /// </summary>
    /// <remarks>
    /// Resolved once per source: the token is read at construction, never logged, and
    /// never included in an exception message. It is sent only to the API-root host
    /// (see <see cref="SendHttpsAsync"/>), never to asset redirect hosts.
    /// </remarks>
    private static string? ReadToken(Func<string, string?> readEnvironment)
    {
        string? githubToken = readEnvironment("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(githubToken))
            return githubToken.Trim();

        string? ghToken = readEnvironment("GH_TOKEN");
        return string.IsNullOrWhiteSpace(ghToken) ? null : ghToken.Trim();
    }

    /// <inheritdoc />
    public async Task<SquadReleaseResult> DownloadAndExtractAsync(
        SquadReleaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateTransport(_apiRoot, "GitHub API origin");
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        string extractionRoot = _paths.GetFullPath(request.DestinationPath, _environment.CurrentDirectory);
        RejectExistingSymbolicLinks(extractionRoot);

        Uri releaseUri = BuildReleaseUri(request.Repository, request.Version);

        // A 404 on the release-tag GET means the pinned version maps to no GitHub tag — a
        // release-state fault the caller resolves by choosing an available version, so it
        // gets the same targeted treatment as a missing asset instead of a raw transport
        // error. GitHub answers 404 for a private or throttled repository too; that
        // ambiguity is accepted (the repository is public) and the message names the exact
        // version and releases page regardless, while every other status keeps the raw
        // failure so a genuine server error is not misread as "version does not exist".
        GitHubRelease release;
        try
        {
            release = await ReadReleaseAsync(releaseUri, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidDataException(
                $"No Kyber-Squad release exists at version '{request.Version}' in '{request.Repository}'. "
                    + $"Squad is released with the CLI under 'v{request.Version}' tags; see "
                    + $"https://github.com/{request.Repository}/releases for available versions.",
                exception);
        }
        if (!string.Equals(release.TagName, $"v{request.Version}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"GitHub returned release tag '{release.TagName}' instead of 'v{request.Version}'.");
        }

        string archiveName = $"kyber-squad-{request.Version}.zip";

        // A Squad bundle is published by the release that carries the matching CLI, and the
        // version is not overridable by design. So "asset missing" always means the release
        // this CLI came from shipped without one — a state every release before Squad
        // packaging was automated is in. Say that, because "not found" alone reads as a
        // network or naming fault the caller could retry their way out of.
        SquadReleaseAsset archive = SelectAsset(
            _allowLoopbackHttp,
            release.Assets,
            archiveName,
            $"Release '{release.TagName}' ships no Squad bundle. Squad is versioned with the "
                + "CLI, so move to a release that carries one with 'kyber-weave update'.");
        SquadReleaseAsset checksumAsset = SelectAsset(_allowLoopbackHttp, release.Assets, ChecksumAssetName);

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

        if (!IsValidReleaseVersion(request.Version))
        {
            throw new ArgumentException(
                "The release version must use valid semantic version format (X.Y.Z or X.Y.Z-prerelease) with no leading zeros.",
                nameof(request));
        }
    }

    /// <summary>
    /// The strict release-tag rule <see cref="ValidateRequest"/> enforces before any
    /// network call: X.Y.Z or X.Y.Z-prerelease, no leading zeros, no build metadata.
    /// </summary>
    internal static bool IsValidReleaseVersion(string version) =>
        ReleaseVersion.IsStrictSemVer(version);

    private Uri BuildReleaseUri(string repository, string version)
    {
        string[] parts = repository.Split('/');
        string relative = $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}" +
            $"/releases/tags/v{Uri.EscapeDataString(version)}";
        Uri releaseUri = new Uri(_apiRoot, relative);
        ValidateTransport(releaseUri, "GitHub release URI");
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
        ValidateTransport(uri, "GitHub release asset URI");
        using HttpResponseMessage response = await SendHttpsAsync(uri, cancellationToken).ConfigureAwait(false);

        // A declared length over the cap is refused before any byte is read; an undeclared or understated one is
        // caught by the capped copy, so a hostile or broken host cannot make the CLI buffer an unbounded asset.
        long? declared = response.Content.Headers.ContentLength;
        if (declared > _maximumAssetBytes)
        {
            throw new InvalidDataException(
                $"The release asset is {declared} bytes, over the {_maximumAssetBytes}-byte limit.");
        }

        Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            return await ReadCappedAsync(body, _maximumAssetBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads <paramref name="source"/> to its end, refusing more than <paramref name="maximumBytes"/>.</summary>
    internal static async Task<byte[]> ReadCappedAsync(Stream source, long maximumBytes, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException($"The release asset is over the {maximumBytes}-byte limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> SendHttpsAsync(
        Uri initialUri,
        CancellationToken cancellationToken)
    {
        Uri currentUri = initialUri;
        for (int redirectCount = 0; redirectCount <= MaximumRedirects; redirectCount++)
        {
            ValidateTransport(currentUri, "HTTP request or redirect");
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("kyber-weave");

            // The token authenticates only the API-root host. Asset downloads redirect
            // to release asset hosts (objects.githubusercontent.com,
            // release-assets.githubusercontent.com, github.com); those requests carry no
            // Authorization header, and the per-request header is rebuilt each hop so a
            // redirect off the API host never inherits it.
            if (_token is not null && IsApiHost(currentUri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            }

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
                        ThrowForStatus(response);
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
            ValidateTransport(currentUri, "HTTP redirect");
        }

        throw new InvalidOperationException(
            $"The release request exceeded the {MaximumRedirects} redirect limit.");
    }

    private bool IsApiHost(Uri uri) =>
        string.Equals(uri.Host, _apiRoot.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _apiRoot.Port;

    /// <summary>
    /// Surfaces the anonymous-rate-limit remedy on 403 only when no token was sent, so
    /// an authenticated caller never gets told to set a token they already set. The
    /// token value itself never appears in the message.
    /// </summary>
    private void ThrowForStatus(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden && _token is null)
        {
            throw new HttpRequestException(
                $"GitHub refused the release request (HTTP 403). Anonymous requests are rate-limited; " +
                $"set GITHUB_TOKEN (or GH_TOKEN) to authenticate and avoid the anonymous rate limit. " +
                $"Request: {response.RequestMessage?.RequestUri?.GetLeftPart(UriPartial.Authority)}",
                inner: null,
                statusCode: HttpStatusCode.Forbidden);
        }

        response.EnsureSuccessStatusCode();
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Redirect or
        HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private void ValidateTransport(Uri uri, string subject) =>
        ValidateTransport(_allowLoopbackHttp, uri, subject);

    /// <summary>Requires HTTPS, except for loopback under an active local-origin override.</summary>
    /// <remarks>
    /// The exemption is keyed on the URI's own authority, not on a mode flag, so a redirect
    /// off the local server still has to be HTTPS even while the override is active.
    /// </remarks>
    private static void ValidateTransport(bool allowLoopbackHttp, Uri uri, string subject)
    {
        if (uri.IsAbsoluteUri && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            return;

        if (allowLoopbackHttp
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && ReleaseOrigin.IsLoopbackAuthority(uri))
        {
            return;
        }

        throw new InvalidOperationException($"{subject} must use HTTPS.");
    }

    private static SquadReleaseAsset SelectAsset(
        bool allowLoopbackHttp,
        IReadOnlyList<GitHubReleaseAsset> assets,
        string assetName,
        string? missingHint = null)
    {
        GitHubReleaseAsset[] matches = assets.Where(
            asset => string.Equals(asset.Name, assetName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1)
        {
            string qualifier = matches.Length == 0 ? "does not contain" : "contains duplicate";
            string hint = matches.Length == 0 && !string.IsNullOrWhiteSpace(missingHint)
                ? " " + missingHint
                : string.Empty;
            throw new InvalidDataException(
                $"The GitHub release {qualifier} exact asset '{assetName}'.{hint}");
        }

        if (!Uri.TryCreate(matches[0].BrowserDownloadUrl, UriKind.Absolute, out Uri? downloadUri))
            throw new InvalidDataException($"Release asset '{assetName}' has an invalid download URI.");

        ValidateTransport(allowLoopbackHttp, downloadUri, $"Release asset '{assetName}' URI");
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

#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
        return new SquadReleaseChecksum(
            assetName,
            exactRow[..64].ToLowerInvariant());
#pragma warning restore CA1308
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

    private IReadOnlyList<ValidatedArchiveEntry> ValidateArchive(
        byte[] archiveBytes,
        string extractionRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using MemoryStream stream = new MemoryStream(archiveBytes, writable: false);
            using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            StringComparer comparison = _isWindows
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

    private ValidatedArchiveEntry ValidateArchivePath(string entryName, string extractionRoot)
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
        string[] segments = portablePath.Split('/');
        int segmentCount = isDirectory ? segments.Length - 1 : segments.Length;
        if (segmentCount == 0 || segments.Take(segmentCount).Any(
            segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"The Squad archive contains an unsafe path: '{entryName}'.");
        }

        string relativePath = string.Join('/', segments.Take(segmentCount));
        string platformPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullExtractionRoot = _paths.GetFullPath(extractionRoot, _environment.CurrentDirectory);
        string containmentPrefix = fullExtractionRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullExtractionRoot
            : fullExtractionRoot + Path.DirectorySeparatorChar;
        // Path.GetFullPath with an explicit base is pure string work (it never reads the process
        // working directory), and it is the containment form code scanners recognise as a
        // Zip Slip guard; the '..' segment refusal above already rejects every escape.
        string candidate = Path.GetFullPath(platformPath, fullExtractionRoot);
        StringComparison comparison = _isWindows
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

    private async Task ExtractAtomicallyAsync(
        byte[] archiveBytes,
        string extractionRoot,
        IReadOnlyList<ValidatedArchiveEntry> entries,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_files.PathExists(extractionRoot))
        {
            throw new IOException(
                $"Squad extraction destination already exists: '{extractionRoot}'.");
        }

        string? parent = Path.GetDirectoryName(extractionRoot);
        if (string.IsNullOrEmpty(parent) || !_files.DirectoryExists(parent))
        {
            throw new DirectoryNotFoundException(
                "The parent directory for the Squad extraction destination must already exist.");
        }

        string stagingRoot = Path.Combine(
            parent,
            $".{Path.GetFileName(extractionRoot)}.kyber-squad-{_ids.NewGuid():N}");
        string stagingPrefix = stagingRoot.EndsWith(Path.DirectorySeparatorChar)
            ? stagingRoot
            : stagingRoot + Path.DirectorySeparatorChar;
        StringComparison comparison = _isWindows
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        try
        {
            _files.CreateDirectory(stagingRoot);
            _onStagingCreated?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            await using MemoryStream stream = new MemoryStream(archiveBytes, writable: false);
            await using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            for (int index = 0; index < archive.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ZipArchiveEntry entry = archive.Entries[index];
                ValidatedArchiveEntry validated = entries[index];
                // The path written is the checked full path itself (Path.GetFullPath with an
                // explicit base is pure string work), so no unchecked form reaches the file system.
                string destination = Path.GetFullPath(
                    validated.RelativePath.Replace('/', Path.DirectorySeparatorChar),
                    stagingRoot);
                if (!destination.StartsWith(stagingPrefix, comparison))
                {
                    throw new InvalidDataException(
                        $"The Squad archive path escapes its extraction root: '{entry.FullName}'.");
                }

                if (validated.IsDirectory)
                {
                    _files.CreateDirectory(destination);
                    continue;
                }

                string destinationDirectory = Path.GetDirectoryName(destination)
                    ?? throw new InvalidDataException("The Squad archive contains an invalid file path.");
                _files.CreateDirectory(destinationDirectory);
                await using Stream source = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using Stream target = _files.OpenWriteNew(destination);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _files.MoveDirectory(stagingRoot, extractionRoot);
        }
        catch
        {
            _files.DeleteTreeNoFollow(stagingRoot);
            throw;
        }
    }

    private void RejectExistingSymbolicLinks(string extractionRoot)
    {
        // Platform temporary roots can themselves be links (for example /var on macOS).
        // The release port owns only the requested destination tree; containment above
        // that boundary belongs to the caller's deployment-root policy.
        if (IsLinkEntry(extractionRoot))
        {
            throw new InvalidOperationException(
                $"Squad extraction rejects an existing symbolic link: '{extractionRoot}'.");
        }

        if (!_files.PathExists(extractionRoot))
            return;

        RejectSymbolicLink(extractionRoot);
        if (_files.DirectoryExists(extractionRoot))
            RejectSymbolicLinksBelow(extractionRoot);
    }

    /// <summary>Whether a path is itself a link, including one whose target is gone, which only a listing of its parent reports.</summary>
    private bool IsLinkEntry(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            return false;

        string name = Path.GetFileName(path);
        StringComparison comparison = _environment.IsFileSystemCaseInsensitive
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return _files.EnumerateEntries(parent).Any(
            entry => entry.IsLink && string.Equals(Path.GetFileName(entry.Path), name, comparison));
    }

    private void RejectSymbolicLinksBelow(string directory)
    {
        foreach (FileSystemEntry entry in _files.EnumerateEntries(directory))
        {
            if (entry.IsLink)
            {
                throw new InvalidOperationException(
                    $"Squad extraction rejects an existing symbolic link: '{entry.Path}'.");
            }

            if (entry.IsDirectory)
                RejectSymbolicLinksBelow(entry.Path);
        }
    }

    private void RejectSymbolicLink(string path)
    {
        if ((_files.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
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
