using System.Runtime.InteropServices;

namespace KyberWeave.Cli.Update;

/// <summary>Maps the running OS and CPU to a published Release RID.</summary>
internal static class PlatformRid
{
    internal static readonly string[] Published =
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
        var os = windows ? "win"
            : linux ? "linux"
            : macos ? "osx"
            : throw new SelfUpdateException("unsupported OS. Published RIDs: " + string.Join(", ", Published));

        var cpu = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new SelfUpdateException($"unsupported architecture: {architecture}. Published RIDs: {string.Join(", ", Published)}")
        };

        var rid = os + "-" + cpu;
        if (Array.IndexOf(Published, rid) < 0)
        {
            throw new SelfUpdateException(
                $"no Release asset for {rid} (supported: {string.Join(", ", Published)})");
        }

        return rid;
    }

    internal static bool IsWindowsRid(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal);
}
