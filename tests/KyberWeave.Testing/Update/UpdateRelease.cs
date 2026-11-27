using System.Security.Cryptography;
using System.Text;
using KyberWeave.Application.Update;

namespace KyberWeave.Testing.Update;

/// <summary>Builds the GitHub release an update downloads, in memory: the archives, their checksum listing and the addresses they are served from.</summary>
internal static class UpdateRelease
{
    /// <summary>Maps one release for a host.</summary>
    /// <param name="version">The release version, without the leading <c>v</c>.</param>
    /// <param name="rid">The host's runtime identifier.</param>
    /// <param name="windows">True to publish <c>.zip</c> archives of <c>.exe</c> binaries.</param>
    /// <param name="withKyberDash">True to publish the KyberDash archive, under its own runtime identifier.</param>
    /// <param name="withArbiter">True to publish the arbiter archive.</param>
    /// <param name="withKyberDashSiblings">True to put the API and MCP binaries in the KyberDash archive.</param>
    /// <returns>The handler serving the release; the caller owns it.</returns>
    internal static ReleaseMapHandler Map(
        string version,
        string rid,
        bool windows,
        bool withKyberDash = false,
        bool withArbiter = false,
        bool withKyberDashSiblings = false)
    {
        ReleaseMapHandler handler = new();
        string tag = "v" + version;
        StringBuilder sums = new();

        AddArchive(handler, sums, tag, "kyber-weave", rid, windows, [("kyber-weave", "new-cli")]);
        AddArchive(handler, sums, tag, "kyber-weave-mcp", rid, windows, [("kyber-weave-mcp", "new-mcp")]);

        if (withKyberDash)
        {
            // Published under the KyberDash RID, so an update that asked for the .NET RID would miss this mapping.
            List<(string BaseName, string Content)> entries = [("kyberdash", "new-kyberdash")];
            if (withKyberDashSiblings)
            {
                entries.Add(("kyberdash-api", "new-kyberdash-api"));
                entries.Add(("kyberdash-mcp", "new-kyberdash-mcp"));
            }

            AddArchive(handler, sums, tag, "kyberdash", PlatformRid.KyberDashRid(rid), windows, entries);
        }

        if (withArbiter)
            AddArchive(handler, sums, tag, "kyber-weave-arbiter", rid, windows, [("kyber-weave-arbiter", "new-arbiter")]);

        SetSums(handler, tag, sums.ToString());
        return handler;
    }

    /// <summary>Maps the latest-release lookup to a release tag.</summary>
    /// <param name="handler">The release's handler.</param>
    /// <param name="version">The release version, without the leading <c>v</c>.</param>
    internal static void MapLatest(ReleaseMapHandler handler, string version) =>
        handler.MapJson(GitHubReleaseClient.LatestApi, $$"""{"tag_name":"v{{version}}","draft":false}""");

    /// <summary>The checksum line for an archive, as <c>sha256sum</c> prints it.</summary>
    /// <param name="archive">The archive bytes.</param>
    /// <param name="archiveName">The archive's file name.</param>
    /// <returns>The line, ending in a newline.</returns>
    internal static string SumLine(byte[] archive, string archiveName) =>
        Convert.ToHexStringLower(SHA256.HashData(archive)) + "  " + archiveName + "\n";

    /// <summary>Replaces the checksum listing of a release.</summary>
    /// <param name="handler">The release's handler.</param>
    /// <param name="tag">The release tag.</param>
    /// <param name="sums">The listing text.</param>
    internal static void SetSums(ReleaseMapHandler handler, string tag, string sums) =>
        handler.MapFile(GitHubReleaseClient.AssetUri(tag, "SHA256SUMS.txt"), Encoding.UTF8.GetBytes(sums));

    private static void AddArchive(
        ReleaseMapHandler handler,
        StringBuilder sums,
        string tag,
        string archiveBaseName,
        string rid,
        bool windows,
        IReadOnlyList<(string BaseName, string Content)> entries)
    {
        ReleaseArchiveEntry[] archiveEntries =
        [
            .. entries.Select(entry => new ReleaseArchiveEntry(
                BinaryInstaller.InstalledFileName(entry.BaseName, windows),
                entry.Content)),
        ];
        byte[] archive = windows ? ReleaseArchiveBuilder.Zip(archiveEntries) : ReleaseArchiveBuilder.TarGz(archiveEntries);
        string archiveName = BinaryInstaller.ArchiveName(archiveBaseName, rid);
        handler.MapFile(GitHubReleaseClient.AssetUri(tag, archiveName), archive);
        sums.Append(SumLine(archive, archiveName));
    }
}
