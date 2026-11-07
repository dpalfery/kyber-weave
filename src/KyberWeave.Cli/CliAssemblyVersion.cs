using System.Reflection;

namespace KyberWeave.Cli;

/// <summary>
/// The CLI assembly version doctor shows, and the version it compares to a probe.
/// </summary>
/// <remarks>
/// The informational version may carry <c>+build</c> metadata and a leading <c>v</c>.
/// A <c>--version</c> probe never emits either, so the comparison drops them. The
/// displayed doctor line keeps the informational version.
/// </remarks>
internal static class CliAssemblyVersion
{
    /// <summary>
    /// The assembly informational version, or the assembly version when the
    /// informational version is missing.
    /// </summary>
    internal static string Informational(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? infoVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            return infoVersion;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>
    /// <see cref="Informational"/> with <c>+build</c> metadata removed and a leading
    /// <c>v</c> trimmed, which is what doctor compares to a probe.
    /// </summary>
    internal static string ForProbe(Assembly assembly) => Normalize(Informational(assembly));

    /// <summary>
    /// Drops <c>+build</c> metadata and a leading <c>v</c> so a probe semver can match
    /// the informational version.
    /// </summary>
    internal static string Normalize(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        int plus = version.IndexOf('+', StringComparison.Ordinal);
        string normalized = plus > 0 ? version[..plus] : version;
        return normalized.Trim().TrimStart('v');
    }
}
