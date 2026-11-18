using System.Runtime.InteropServices;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Cli.Update;

/// <summary>Maps the running OS and CPU to a published Release RID.</summary>
public static class PlatformRid
{
    private static readonly string[] Published =
    [
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
        "win-x64"
    ];

    /// <summary>Maps the running operating system, as the environment reports it, and the process architecture to a published RID.</summary>
    /// <param name="environment">Reports the operating system.</param>
    /// <returns>The RID.</returns>
    /// <exception cref="SelfUpdateException">The platform has no published RID.</exception>
    public static string Detect(IProcessEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Detect(environment.OperatingSystem, RuntimeInformation.ProcessArchitecture);
    }

    /// <summary>Maps an operating system kind and a processor architecture to a published RID.</summary>
    /// <param name="operatingSystem">The operating system.</param>
    /// <param name="architecture">The processor architecture.</param>
    /// <returns>The RID.</returns>
    /// <exception cref="SelfUpdateException">The platform has no published RID.</exception>
    public static string Detect(OperatingSystemKind operatingSystem, Architecture architecture) => Detect(
        operatingSystem == OperatingSystemKind.Windows,
        operatingSystem == OperatingSystemKind.Linux,
        operatingSystem == OperatingSystemKind.MacOS,
        architecture);

    public static string Detect(bool windows, bool linux, bool macos, Architecture architecture)
    {
        string os = windows ? "win"
            : linux ? "linux"
            : macos ? "osx"
            : throw new SelfUpdateException("unsupported OS. Published RIDs: " + string.Join(", ", Published));

        string cpu = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new SelfUpdateException($"unsupported architecture: {architecture}. Published RIDs: {string.Join(", ", Published)}")
        };

        string rid = os + "-" + cpu;
        if (Array.IndexOf(Published, rid) < 0)
        {
            throw new SelfUpdateException(
                $"no Release asset for {rid} (supported: {string.Join(", ", Published)})");
        }

        return rid;
    }

    public static bool IsWindowsRid(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>Maps a Kyber-Weave RID to the KyberDash Node SEA RID for the same platform.</summary>
    /// <remarks>
    /// KyberDash ships as a Node single-executable, and Node's stable RID set names macOS
    /// <c>darwin-*</c> where .NET names it <c>osx-*</c>; the Linux and Windows names coincide.
    /// <c>kyber_weave_kyberdash_rid</c> in <c>scripts/install.sh</c> is the same mapping for the
    /// first-install path. An unrecognised RID is returned verbatim rather than reshaped into a
    /// name no release publishes.
    /// </remarks>
    public static string KyberDashRid(string rid) => rid switch
    {
        "osx-x64" => "darwin-x64",
        "osx-arm64" => "darwin-arm64",
        _ => rid
    };
}
