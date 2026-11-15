using System.Reflection;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Resolves the dashboard SPA: <c>KYBERDASH_DASH_DIR</c> (or an explicit override) on disk first, then a
/// <c>kyberdash-web/1</c> bundle embedded in any loaded assembly as a <c>web.json</c> resource.
/// </summary>
public class WebAssetRepository : IWebAssetRepository
{
    /// <summary>Environment variable overriding the built dashboard directory.</summary>
    public const string DashDirectoryEnvironmentVariable = "KYBERDASH_DASH_DIR";

    /// <summary>Format marker of the single-file embedded dashboard bundle.</summary>
    public const string BundleFormat = "kyberdash-web/1";

    /// <summary>File name the embedded bundle resource ends with.</summary>
    public const string BundleResourceSuffix = "web.json";

    private static readonly Lock BundleGate = new();
    private static IReadOnlyDictionary<string, byte[]>? cachedBundle;
    private static bool bundleProbed;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".HTML"] = "text/html; charset=utf-8",
        [".JS"] = "text/javascript; charset=utf-8",
        [".CSS"] = "text/css; charset=utf-8",
        [".SVG"] = "image/svg+xml",
        [".JSON"] = "application/json; charset=utf-8",
        [".WOFF2"] = "font/woff2",
        [".WOFF"] = "font/woff",
        [".PNG"] = "image/png",
        [".ICO"] = "image/x-icon",
        [".MAP"] = "application/json",
    };

    /// <summary>Initializes a repository that reads the dashboard directory from the environment.</summary>
    public WebAssetRepository()
        : this(null)
    {
    }

    /// <summary>Initializes a repository whose disk dashboard directory is <paramref name="dashboardDirectory"/>.</summary>
    /// <param name="dashboardDirectory">Configured dashboard directory, or <see langword="null"/> to use the environment.</param>
    public WebAssetRepository(string? dashboardDirectory)
    {
        ConfiguredDirectory = string.IsNullOrWhiteSpace(dashboardDirectory) ? null : Path.GetFullPath(dashboardDirectory);
    }

    /// <summary>Directory configured on this instance, before the environment variable is consulted.</summary>
    public string? ConfiguredDirectory { get; }

    /// <inheritdoc />
    public WebAssetDocument? TryResolveAsset(string requestPath, string? overrideDir = null)
    {
        ArgumentNullException.ThrowIfNull(requestPath);

        string relative = NormalizeAssetKey(Uri.UnescapeDataString(requestPath));
        if (relative.Length == 0)
            relative = "index.html";

        string? diskDirectory = ResolveDiskDirectory(overrideDir);
        if (diskDirectory is not null)
            return ReadFromDisk(diskDirectory, relative);

        IReadOnlyDictionary<string, byte[]>? bundle = GetEmbeddedBundle();
        if (bundle is not null)
        {
            if (bundle.TryGetValue(relative, out byte[]? bytes))
                return BuildDocument(relative, bytes);

            if (bundle.TryGetValue("index.html", out byte[]? indexBytes))
                return BuildDocument("index.html", indexBytes);
        }

        return null;
    }

    /// <summary>
    /// Parses a <c>kyberdash-web/1</c> bundle into assets keyed by normalized relative POSIX path.
    /// </summary>
    /// <param name="rawJson">Bundle JSON text.</param>
    /// <returns>The assets when the bundle is well formed and carries <c>index.html</c>; otherwise <see langword="null"/>.</returns>
    public static IReadOnlyDictionary<string, byte[]>? ParseBundle(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out JsonElement formatEl)
                || formatEl.ValueKind != JsonValueKind.String
                || !string.Equals(formatEl.GetString(), BundleFormat, StringComparison.Ordinal)
                || !root.TryGetProperty("files", out JsonElement filesEl)
                || filesEl.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            Dictionary<string, byte[]> assets = new(StringComparer.Ordinal);
            foreach (JsonProperty prop in filesEl.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                    continue;

                string? base64 = prop.Value.GetString();
                if (base64 is null)
                    continue;

                string key = NormalizeAssetKey(prop.Name);
                if (key.Length == 0)
                    continue;

                assets[key] = Convert.FromBase64String(base64);
            }

            return assets.ContainsKey("index.html") ? assets : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Drops the memoized embedded bundle so the next resolve scans loaded assemblies again.</summary>
    public static void ClearEmbeddedBundleCache()
    {
        lock (BundleGate)
        {
            cachedBundle = null;
            bundleProbed = false;
        }
    }

    private string? ResolveDiskDirectory(string? overrideDir)
    {
        string? candidate = overrideDir;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = ConfiguredDirectory;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Environment.GetEnvironmentVariable(DashDirectoryEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return File.Exists(Path.Combine(full, "index.html")) ? full : null;
    }

    private static WebAssetDocument? ReadFromDisk(string directory, string relative)
    {
        if (ContainsTraversalSegment(relative))
            return null;

        string rootWithSeparator = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!candidate.Equals(directory, StringComparison.Ordinal)
            && !candidate.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            return null;
        }

        string indexPath = Path.Combine(directory, "index.html");
        try
        {
            if (File.Exists(candidate) && new FileInfo(candidate).LinkTarget is null)
                return BuildDocument(relative, File.ReadAllBytes(candidate));

            if (File.Exists(indexPath) && new FileInfo(indexPath).LinkTarget is null)
                return BuildDocument("index.html", File.ReadAllBytes(indexPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static IReadOnlyDictionary<string, byte[]>? GetEmbeddedBundle()
    {
        lock (BundleGate)
        {
            if (bundleProbed)
                return cachedBundle;

            bundleProbed = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? resourceName = FindBundleResource(assembly);
                if (resourceName is null)
                    continue;

                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is null)
                    continue;

                using StreamReader reader = new(stream, System.Text.Encoding.UTF8);
                cachedBundle = ParseBundle(reader.ReadToEnd());
                if (cachedBundle is not null)
                    return cachedBundle;
            }

            return cachedBundle;
        }
    }

    private static string? FindBundleResource(Assembly assembly)
    {
        string[] names;
        try
        {
            names = assembly.GetManifestResourceNames();
        }
        catch (Exception exception) when (exception is NotSupportedException or IOException)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (string.Equals(name, BundleResourceSuffix, StringComparison.Ordinal)
                || name.EndsWith("." + BundleResourceSuffix, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }

    private static WebAssetDocument BuildDocument(string relativePath, byte[] bytes)
    {
        string extension = Path.GetExtension(relativePath);
        string contentType = ContentTypes.TryGetValue(extension.ToUpperInvariant(), out string? known)
            ? known
            : "application/octet-stream";
        return new WebAssetDocument(contentType, bytes, IsCompressed: false, ResolvedPath: relativePath);
    }

    private static string NormalizeAssetKey(string rawPath)
    {
        ArgumentNullException.ThrowIfNull(rawPath);
        string normalized = rawPath.Replace('\\', '/').TrimStart('/');
        return string.Equals(normalized, ".", StringComparison.Ordinal)
            ? string.Empty
            : normalized;
    }

    private static bool ContainsTraversalSegment(string posixPath)
    {
        foreach (string segment in posixPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, "..", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
