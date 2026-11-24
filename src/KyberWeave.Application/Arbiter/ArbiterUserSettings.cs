using System.Diagnostics.CodeAnalysis;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Parsing;
using YamlDotNet.Core;

namespace KyberWeave.Core.Arbiter;

/// <summary>Reads the per-user Arbiter override from the operator's home directory.</summary>
/// <remarks>
/// The override lives at <c>~/.config/kyber-weave/arbiter.yml</c> and may hold
/// <c>provider:</c> and nothing else; its fields replace the repository's
/// <c>arbiter.provider</c> field by field. The home directory is injected rather than
/// read from the environment so hooks, servers and tests resolve the same file. A
/// missing file means no override. The key never appears here — this file carries only
/// non-secret provider fields — so diagnostics may name the file freely.
/// </remarks>
public static class ArbiterUserSettings
{
    private const int MaxCandidateDisplayLength = 200;

    /// <summary>Locates the override file under an injected home directory.</summary>
    public static string GetPath(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return Path.Combine(homeDirectory, ".config", "kyber-weave", "arbiter.yml");
    }

    /// <summary>
    /// Trims the effective provider's endpoint and derives its <c>scheme://host[:port]</c> origin,
    /// or builds a <c>KW-ARB-CONFIG-001</c> error naming the user override file or host configuration
    /// when the endpoint is blank or not an absolute <c>http(s)</c> URL.
    /// </summary>
    /// <param name="repositoryProvider">The repository provider before the per-user override was applied.</param>
    /// <param name="effectiveProvider">The merged provider whose endpoint is resolved.</param>
    /// <param name="homeDirectory">The home directory where the per-user override lives.</param>
    /// <param name="configPath">The loaded host configuration path, or null when none was recorded.</param>
    /// <param name="endpoint">The trimmed endpoint URL when valid.</param>
    /// <param name="origin">The derived endpoint origin when valid.</param>
    /// <param name="error">The configuration error message when invalid.</param>
    /// <returns><see langword="true"/> when the endpoint and origin resolved; otherwise <see langword="false"/>.</returns>
    public static bool TryResolveEndpointOrigin(
        ArbiterProviderConfig repositoryProvider,
        ArbiterProviderConfig effectiveProvider,
        string homeDirectory,
        string? configPath,
        out string endpoint,
        out string origin,
        [NotNullWhen(false)] out string? error) =>
        TryResolveEndpointOriginCore(
            repositoryProvider,
            effectiveProvider,
            endpointFromOverride: null,
            homeDirectory,
            configPath,
            out endpoint,
            out origin,
            out error);

    /// <summary>
    /// Trims the effective provider's endpoint and derives its <c>scheme://host[:port]</c> origin,
    /// or builds a <c>KW-ARB-CONFIG-001</c> error naming the user override file when
    /// <paramref name="endpointFromOverride"/> is <see langword="true"/> or the host configuration otherwise.
    /// </summary>
    /// <param name="repositoryProvider">The repository provider before the per-user override was applied.</param>
    /// <param name="effectiveProvider">The merged provider whose endpoint is resolved.</param>
    /// <param name="endpointFromOverride">Whether the per-user override explicitly supplied <c>provider.endpoint</c>.</param>
    /// <param name="homeDirectory">The home directory where the per-user override lives.</param>
    /// <param name="configPath">The loaded host configuration path, or null when none was recorded.</param>
    /// <param name="endpoint">The trimmed endpoint URL when valid.</param>
    /// <param name="origin">The derived endpoint origin when valid.</param>
    /// <param name="error">The configuration error message when invalid.</param>
    /// <returns><see langword="true"/> when the endpoint and origin resolved; otherwise <see langword="false"/>.</returns>
    public static bool TryResolveEndpointOrigin(
        ArbiterProviderConfig repositoryProvider,
        ArbiterProviderConfig effectiveProvider,
        bool endpointFromOverride,
        string homeDirectory,
        string? configPath,
        out string endpoint,
        out string origin,
        [NotNullWhen(false)] out string? error) =>
        TryResolveEndpointOriginCore(
            repositoryProvider,
            effectiveProvider,
            endpointFromOverride,
            homeDirectory,
            configPath,
            out endpoint,
            out origin,
            out error);

    internal static string NormalizeEndpoint(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? string.Empty : endpoint.Trim();

    internal static bool EndpointsEqual(string? left, string? right) =>
        string.Equals(NormalizeEndpoint(left), NormalizeEndpoint(right), StringComparison.Ordinal);

    private static bool TryResolveEndpointOriginCore(
        ArbiterProviderConfig repositoryProvider,
        ArbiterProviderConfig effectiveProvider,
        bool? endpointFromOverride,
        string homeDirectory,
        string? configPath,
        out string endpoint,
        out string origin,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(repositoryProvider);
        ArgumentNullException.ThrowIfNull(effectiveProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);

        string candidate = NormalizeEndpoint(effectiveProvider.Endpoint);
        string? resolvedOrigin = candidate.Length == 0
            ? null
            : ArbiterKeyResolver.GetOrigin(candidate);
        if (resolvedOrigin is not null)
        {
            endpoint = candidate;
            origin = resolvedOrigin;
            error = null;
            return true;
        }

        endpoint = string.Empty;
        origin = string.Empty;
        bool fromOverride = endpointFromOverride
            ?? !EndpointsEqual(effectiveProvider.Endpoint, repositoryProvider.Endpoint);
        string source = fromOverride
            ? $"The user override '{GetPath(homeDirectory)}'"
            : !string.IsNullOrWhiteSpace(configPath)
                ? $"The host configuration '{configPath}'"
                : "The host configuration";
        string displayCandidate = FormatCandidateForError(candidate);
        error = $"[{RuleValidator.MalformedSection}] {source} has an invalid arbiter.provider.endpoint '{displayCandidate}'; expected an absolute http(s) URL.";
        return false;
    }

    private static string FormatCandidateForError(string candidate)
    {
        string oneLine = candidate.ReplaceLineEndings(" ");
        return oneLine.Length > MaxCandidateDisplayLength
            ? oneLine[..MaxCandidateDisplayLength] + "..."
            : oneLine;
    }

    /// <summary>
    /// Parses the override file's text into its <c>provider:</c> mapping and checks that it holds nothing else.
    /// </summary>
    /// <param name="yaml">The file's text.</param>
    /// <param name="path">The file's path, named in every failure.</param>
    /// <returns>The <c>provider:</c> mapping, or null when the file declares none.</returns>
    /// <exception cref="YamlException">The text is malformed or holds a key other than <c>provider</c>.</exception>
    internal static ArbiterProviderYaml? ParseProvider(string yaml, string path)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Dictionary<string, object?>? raw;
        ArbiterYamlSection? section;
        try
        {
            raw = MarkdownFrontmatterReader.Deserializer.Deserialize<Dictionary<string, object?>?>(yaml);
            section = MarkdownFrontmatterReader.Deserializer.Deserialize<ArbiterYamlSection?>(yaml);
        }
        catch (YamlException exception)
        {
            throw new YamlException(
                $"[{RuleValidator.MalformedSection}] The user override '{path}' is malformed. {exception.Message}",
                exception);
        }

        // The deserializer ignores unmatched properties by design, so unknown top-level
        // keys are detected against the raw mapping instead of the bound section.
        if (raw is not null)
        {
            foreach (string key in raw.Keys)
            {
                if (!string.Equals(key, "provider", StringComparison.Ordinal))
                {
                    throw new YamlException(
                        $"[{RuleValidator.UserOverrideKey}] The user override '{path}' holds '{key}'; it may hold provider and nothing else.");
                }
            }
        }

        IReadOnlyList<Diagnostic> findings = RuleValidator.ValidateUserOverride(section, path);
        if (findings.Count > 0)
        {
            throw new YamlException(
                $"[{RuleValidator.UserOverrideKey}] The user override '{path}' holds '{findings[0].Subject}'; it may hold provider and nothing else.");
        }

        return section?.Provider;
    }
}
