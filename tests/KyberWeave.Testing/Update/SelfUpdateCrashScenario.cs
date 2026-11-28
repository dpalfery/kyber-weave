using System.Net;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using KyberWeave.Application.Update;
using KyberWeave.Data.Update;
using KyberWeave.Domain.Update;

namespace KyberWeave.Testing.Update;

/// <summary>
/// One self-update the crash suite runs: an installed kyber-weave and kyber-weave-mcp at the old version, a release
/// that carries both at the new one, and the real <c>SelfUpdater</c> over the real <c>SelfUpdateSystem</c> with only
/// the processes faked.
/// </summary>
internal sealed class SelfUpdateCrashScenario(bool windows)
{
    private const string Tag = "v0.2.0";
    private const string OldVersion = "0.1.0";
    private const string NewVersion = "0.2.0";

    /// <summary>Gets whether the host is Windows, which takes the move-aside path and a <c>.zip</c> archive.</summary>
    public bool Windows { get; } = windows;

    /// <summary>Gets the running image's installed name.</summary>
    public string Cli => BinaryInstaller.InstalledFileName("kyber-weave", Windows);

    /// <summary>Gets the MCP binary's installed name.</summary>
    public string Mcp => BinaryInstaller.InstalledFileName("kyber-weave-mcp", Windows);

    /// <summary>Gets the two installed names, the running image first.</summary>
    public IReadOnlyList<string> Binaries => [Cli, Mcp];

    /// <summary>Gets the bytes of an image: large enough that a partial write differs from a whole one.</summary>
    /// <param name="version">Old or new.</param>
    /// <param name="name">The installed name.</param>
    /// <returns>The image.</returns>
    public static byte[] Image(string version, string name) =>
        Encoding.UTF8.GetBytes($"{version} image of {name}:" + new string(version[0], 4000));

    /// <summary>Writes the old install.</summary>
    /// <param name="workspace">The workspace.</param>
    public void SeedOldInstall(SelfUpdateCrashWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        foreach (string name in Binaries)
        {
            workspace.Files.WriteAllBytes(Path.Combine(workspace.Install, name), Image("old", name));
        }
    }

    /// <summary>Gets the tree after a whole update.</summary>
    /// <returns>The new images by name.</returns>
    public Dictionary<string, byte[]> NewTree() => Binaries.ToDictionary(name => name, name => Image("new", name), StringComparer.Ordinal);

    /// <summary>Gets the tree before the update.</summary>
    /// <returns>The old images by name.</returns>
    public Dictionary<string, byte[]> OldTree() => Binaries.ToDictionary(name => name, name => Image("old", name), StringComparer.Ordinal);

    /// <summary>Runs the update the way a fresh process would, naming the version the image on disk reports.</summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="installedVersion">The version the running image reports.</param>
    /// <returns>The outcome.</returns>
    public async Task<SelfUpdateOutcome> RunAsync(SelfUpdateCrashWorkspace workspace, string installedVersion)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string rid = Windows ? "win-x64" : "linux-x64";
        SelfUpdateHost host = new(Path.Combine(workspace.Install, Cli), installedVersion, rid, Windows, IsMacOs: false);
        ScriptedProcessExecutor processes = new(_ => new ProcessResult(0, string.Empty, string.Empty));
        SelfUpdateSystem system = new(
            workspace.Files,
            workspace.Paths,
            processes,
            new FakeConsoleProcessLauncher(),
            workspace.Modes);
        using ReleaseHandler handler = new(this, rid);
        using SelfUpdater updater = new(handler, host, workspace.Files, workspace.Paths, workspace.Environment, system);
        return await updater
            .RunAsync(new SelfUpdateOptions(NewVersion, ReleaseCandidate: false, NoMcp: false, NoKyberDash: true, NoArbiter: true))
            .ConfigureAwait(false);
    }

    /// <summary>Gets the version the image installed as the running binary reports.</summary>
    /// <param name="tree">The installed tree.</param>
    /// <returns>The new version when the running image is the new one; the old version otherwise.</returns>
    public string VersionOf(IReadOnlyDictionary<string, byte[]> tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.TryGetValue(Cli, out byte[]? image) && image.AsSpan().SequenceEqual(Image("new", Cli))
            ? NewVersion
            : OldVersion;
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _map = new(StringComparer.Ordinal);

        public ReleaseHandler(SelfUpdateCrashScenario scenario, string rid)
        {
            List<string> sums = [];
            foreach (string baseName in new[] { "kyber-weave", "kyber-weave-mcp" })
            {
                string installed = BinaryInstaller.InstalledFileName(baseName, scenario.Windows);
                ReleaseArchiveEntry entry = new(installed, Encoding.UTF8.GetString(Image("new", installed)));
                byte[] archive = scenario.Windows ? ReleaseArchiveBuilder.Zip(entry) : ReleaseArchiveBuilder.TarGz(entry);
                string archiveName = BinaryInstaller.ArchiveName(baseName, rid);
                _map[GitHubReleaseClient.AssetUri(Tag, archiveName).AbsoluteUri] = archive;
                sums.Add(Convert.ToHexStringLower(SHA256.HashData(archive)) + "  " + archiveName);
            }

            _map[GitHubReleaseClient.AssetUri(Tag, "SHA256SUMS.txt").AbsoluteUri] = Encoding.UTF8.GetBytes(string.Join('\n', sums));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_map.TryGetValue(request.RequestUri?.AbsoluteUri ?? string.Empty, out byte[]? bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }
}
