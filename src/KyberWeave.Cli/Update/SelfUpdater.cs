using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace KyberWeave.Cli.Update;

/// <summary>
/// Resolves a GitHub Release and replaces the running CLI (and sibling MCP) in place.
/// </summary>
/// <remarks>
/// This does not shell out to <c>install.sh</c>: replacing a running image, Windows
/// <c>.zip</c> assets, and unit tests all need a C# path. The script remains the
/// documented first-install channel and reads the same Release assets.
/// </remarks>
internal sealed class SelfUpdater : IDisposable
{
    private const string CliBaseName = "kyber-weave";
    private const string McpBaseName = "kyber-weave-mcp";

    private readonly GitHubReleaseClient _releases;
    private readonly SelfUpdateHost _host;
    private readonly Action<string> _log;

    internal SelfUpdater(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        Action<string>? log = null,
        Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _log = log ?? (_ => { });
        _releases = new GitHubReleaseClient(
            handler,
            host.CurrentVersion,
            readEnvironment ?? Environment.GetEnvironmentVariable);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient takes ownership of the handler (disposeHandler: true).")]
    internal static SelfUpdater Create(SelfUpdateHost host, Action<string>? log = null) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = true }, host, log);

    internal SelfUpdateOutcome Run(SelfUpdateOptions options)
    {
        try
        {
            return RunCore(options);
        }
        catch (SelfUpdateException ex)
        {
            return new SelfUpdateOutcome(1, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return new SelfUpdateOutcome(
                1,
                "the GitHub request timed out. Check the network connection and retry.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new SelfUpdateOutcome(1, ex.Message);
        }
    }

    public void Dispose() => _releases.Dispose();

    private SelfUpdateOutcome RunCore(SelfUpdateOptions options)
    {
        if (options.ReleaseCandidate && !string.IsNullOrWhiteSpace(options.Version))
        {
            throw new SelfUpdateException(
                "--release-candidate cannot be combined with a version. Pin with 'kyber-weave update 0.2.0-rc.1' or omit the version.");
        }

        EnsureReleaseInstallChannel();

        string version;
        if (!string.IsNullOrWhiteSpace(options.Version))
        {
            version = ReleaseVersion.Normalize(options.Version);
        }
        else if (options.ReleaseCandidate)
        {
            _log("resolving latest release (including candidates)…");
            version = _releases.ResolveNewestListed();
        }
        else
        {
            _log("resolving latest release…");
            version = _releases.ResolveLatestStable();
        }

        string current = ReleaseVersion.Normalize(_host.CurrentVersion);
        if (string.Equals(current, version, StringComparison.Ordinal))
            return new SelfUpdateOutcome(0, $"already on {version}");

        string tag = ReleaseVersion.Tag(version);
        bool windows = _host.IsWindows || PlatformRid.IsWindowsRid(_host.Rid);
        DirectoryInfo work = Directory.CreateTempSubdirectory("kyber-weave-update-");
        try
        {
            _log($"installing {tag} ({_host.Rid}) → {_host.InstallDirectory}");
            string sums = _releases.DownloadChecksums(tag);
            List<(string BaseName, string ExtractedPath, string Destination)> staged =
            [
                StageBinary(CliBaseName, tag, windows, sums, work.FullName)
            ];
            if (!options.NoMcp)
                staged.Add(StageBinary(McpBaseName, tag, windows, sums, work.FullName));

            DirectoryInfo backupDir = Directory.CreateTempSubdirectory("kyber-weave-backup-");
            try
            {
                List<(string BackupPath, string Destination, bool Existed)> backups = [];
                foreach ((string BaseName, string ExtractedPath, string Destination) item in staged)
                {
                    string backupFile = Path.Combine(backupDir.FullName, item.BaseName + ".bak");
                    bool existed = File.Exists(item.Destination);
                    if (existed)
                    {
                        File.Copy(item.Destination, backupFile, overwrite: true);
                    }
                    backups.Add((backupFile, item.Destination, existed));
                }

                try
                {
                    foreach ((string BaseName, string ExtractedPath, string Destination) item in staged)
                        CommitBinary(item.BaseName, tag, windows, item.ExtractedPath, item.Destination);
                }
                catch
                {
                    foreach ((string BackupPath, string Destination, bool Existed) backup in backups)
                    {
                        try
                        {
                            if (backup.Existed && File.Exists(backup.BackupPath))
                            {
                                File.Copy(backup.BackupPath, backup.Destination, overwrite: true);
                            }
                            else if (!backup.Existed && File.Exists(backup.Destination))
                            {
                                File.Delete(backup.Destination);
                            }
                        }
                        catch
                        {
                            // Best-effort rollback
                        }
                    }

                    throw;
                }
            }
            finally
            {
                try
                {
                    backupDir.Delete(true);
                }
                catch (IOException)
                {
                }
            }
        }
        finally
        {
            try
            {
                work.Delete(true);
            }
            catch (IOException)
            {
                // Temp leftovers are harmless; the binaries are already in place.
            }
        }

        string installed = Path.Combine(
            _host.InstallDirectory,
            BinaryInstaller.InstalledFileName(CliBaseName, windows));
        return new SelfUpdateOutcome(0, $"updated kyber-weave {version} → {installed}");
    }

    private (string BaseName, string ExtractedPath, string Destination) StageBinary(
        string baseName,
        string tag,
        bool windows,
        string sums,
        string workDirectory)
    {
        string archiveName = BinaryInstaller.ArchiveName(baseName, _host.Rid);
        string archivePath = Path.Combine(workDirectory, archiveName);
        _log($"downloading {archiveName}…");
        _releases.DownloadAsset(tag, archiveName, archivePath);

        string expected = ChecksumVerifier.ExpectedHex(sums, archiveName);
        byte[] hash;
        using (FileStream archive = File.OpenRead(archivePath))
            hash = SHA256.HashData(archive);
        ChecksumVerifier.Verify(expected, hash, archiveName);

        string extractDir = Path.Combine(workDirectory, baseName + "-extract");
        BinaryInstaller.ExtractArchive(archivePath, extractDir, windows);

        string extractedName = BinaryInstaller.InstalledFileName(baseName, windows);
        string extractedPath = FindExtractedBinary(extractDir, extractedName);
        string destination = Path.Combine(_host.InstallDirectory, extractedName);
        return (baseName, extractedPath, destination);
    }

    private void CommitBinary(
        string baseName,
        string tag,
        bool windows,
        string extractedPath,
        string destination)
    {
        BinaryInstaller.Replace(extractedPath, destination, windows);
        if (_host.IsMacOs)
        {
            try
            {
                BinaryInstaller.ClearMacQuarantine(destination);
            }
            catch (Exception)
            {
                // Non-fatal best-effort
            }
        }

        _log($"installed {baseName} {ReleaseVersion.Normalize(tag)} → {destination}");
    }

    private void EnsureReleaseInstallChannel()
    {
        if (string.IsNullOrWhiteSpace(_host.ProcessPath))
            throw new SelfUpdateException("could not determine the running executable path.");

        string fileName = Path.GetFileName(_host.ProcessPath);
        if (fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new SelfUpdateException(
                "this looks like `dotnet run`, not a published Release binary. Install with scripts/install.sh, then run kyber-weave update.");
        }

        if (IsDotnetToolInstall(_host.ProcessPath))
        {
            throw new SelfUpdateException(
                "this looks like a `dotnet tool` install. Update that channel with `dotnet tool update`, or install the Release binary with scripts/install.sh.");
        }

        string directory = _host.InstallDirectory;
        if (!Directory.Exists(directory) || !CanWriteDirectory(directory))
        {
            throw new SelfUpdateException(
                $"no write permission for {directory}. Re-run from a writable Release install, or reinstall with scripts/install.sh --install-dir <dir>.");
        }
    }

    internal static bool IsDotnetToolInstall(string processPath)
    {
        string normalized = processPath.Replace('\\', '/');
        return normalized.Contains("/.dotnet/tools/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/.dotnet/tools", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool CanWriteDirectory(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".kyber-weave-update-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FindExtractedBinary(string extractDir, string fileName)
    {
        string direct = Path.Combine(extractDir, fileName);
        if (File.Exists(direct))
            return direct;

        string[] matches = Directory.GetFiles(extractDir, fileName, SearchOption.AllDirectories);
        if (matches.Length == 1)
            return matches[0];

        throw new SelfUpdateException($"archive did not contain {fileName}");
    }
}
