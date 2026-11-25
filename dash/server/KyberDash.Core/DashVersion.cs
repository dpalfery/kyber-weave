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
    /// <summary>The plain semantic version this build reports, without <c>+metadata</c>.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        string informational = typeof(DashVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? throw new InvalidOperationException("KyberDash.Core carries no AssemblyInformationalVersion attribute.");

        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? informational[..plus] : informational;
    }
}
