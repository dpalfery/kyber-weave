using System.Reflection;

namespace KyberWeave.Cli;

/// <summary>
/// The CLI assembly version doctor shows, and the version it compares to a probe.
/// </summary>
/// <remarks>
/// The informational version may carry <c>+build</c> metadata and a leading <c>v</c>
/// or <c>V</c>. A <c>--version</c> probe never emits either, so the comparison drops
/// them. The displayed doctor line keeps the informational version.
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
    /// <c>v</c> or <c>V</c> trimmed, which is what doctor compares to a probe.
    /// </summary>
    internal static string ForProbe(Assembly assembly) => Normalize(Informational(assembly));

    /// <summary>
    /// Drops <c>+build</c> metadata and one leading <c>v</c> or <c>V</c> so a probe
    /// semver can match the informational version.
    /// </summary>
    /// <remarks>
    /// This is the only prefix and build-metadata cut. Release-tag checks in
    /// <c>ReleaseVersion</c> run after it, so the two cannot disagree on <c>v</c>,
    /// <c>V</c>, or <c>+</c>.
    /// </remarks>
    internal static string Normalize(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        string trimmed = version.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        int plus = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        return trimmed;
    }
}
