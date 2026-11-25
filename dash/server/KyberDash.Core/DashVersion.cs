using System.Reflection;

namespace KyberDash.Core;

/// <summary>The one version every KyberDash binary reports.</summary>
/// <remarks>
/// The SDK stamps the informational version of the KyberDash.Core assembly from the single
/// <c>Version</c> in <c>Directory.Build.props</c>. Every KyberDash project references this assembly,
/// so one build reports one version. The SDK appends <c>+metadata</c> (the source revision) to that
/// informational version; it is stripped so the reported version is the plain release version.
/// There is no literal fallback: the SDK always emits the attribute, and a build without it is broken.
/// </remarks>
public static class DashVersion
{
    private static readonly Lazy<string> Cached = new(() => FromAssembly(typeof(DashVersion).Assembly));

    /// <summary>The plain semantic version this build reports, without <c>+metadata</c>.</summary>
    /// <exception cref="InvalidOperationException">The build carries no informational version.</exception>
    /// <remarks>
    /// Resolved on first use rather than in a static initializer: a throw from a static initializer
    /// surfaces as a <see cref="TypeInitializationException"/> on every later access, which no caller
    /// can map to a clear message. Here the throw is an ordinary <see cref="InvalidOperationException"/>.
    /// </remarks>
    public static string Current => Cached.Value;

    /// <summary>Reads the plain version from one assembly's informational version.</summary>
    /// <param name="assembly">The assembly stamped by the SDK.</param>
    /// <returns>The version without <c>+metadata</c>.</returns>
    /// <exception cref="InvalidOperationException">The assembly carries no informational version.</exception>
    public static string FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? throw new InvalidOperationException(
                $"{assembly.GetName().Name} carries no AssemblyInformationalVersion attribute; this build is broken.");

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
