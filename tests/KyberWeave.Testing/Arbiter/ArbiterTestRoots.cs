namespace KyberWeave.Tests.Arbiter;

/// <summary>Builds platform-qualified directories for tests that run over the in-memory file system.</summary>
internal static class ArbiterTestRoots
{
    private static readonly string FileSystemRoot = Path.GetPathRoot(Path.GetTempPath())!;

    /// <summary>The root of the volume the host's temporary directory lives on.</summary>
    internal static string Root => FileSystemRoot;

    /// <summary>A full path of <paramref name="segments"/> under the volume root.</summary>
    /// <param name="segments">The directory names below the root.</param>
    internal static string Under(params string[] segments) =>
        Path.GetFullPath(Path.Combine([FileSystemRoot, .. segments]));
}
