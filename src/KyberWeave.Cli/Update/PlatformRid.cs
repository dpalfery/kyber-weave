using System.Runtime.InteropServices;

namespace KyberWeave.Cli.Update;

/// <summary>Maps the running OS and CPU to a published Release RID.</summary>
internal static class PlatformRid
{
    private static readonly string[] Published =
    [
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64",
        "win-x64"
    ];

    internal static string Detect() => Detect(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        OperatingSystem.IsMacOS(),
        RuntimeInformation.ProcessArchitecture);

    internal static string Detect(bool windows, bool linux, bool macos, Architecture architecture)
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

    internal static bool IsWindowsRid(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal);

    /// <summary>Maps a Kyber-Weave RID to the KyberDash Node SEA RID for the same platform.</summary>
    /// <remarks>
    /// KyberDash ships as a Node single-executable, and Node's stable RID set names macOS
    /// <c>darwin-*</c> where .NET names it <c>osx-*</c>; the Linux and Windows names coincide.
    /// <c>kyber_weave_kyberdash_rid</c> in <c>scripts/install.sh</c> is the same mapping for the
    /// first-install path. An unrecognised RID is returned verbatim rather than reshaped into a
    /// name no release publishes.
    /// </remarks>
    internal static string KyberDashRid(string rid) => rid switch
    {
        "osx-x64" => "darwin-x64",
        "osx-arm64" => "darwin-arm64",
        _ => rid
    };
}
