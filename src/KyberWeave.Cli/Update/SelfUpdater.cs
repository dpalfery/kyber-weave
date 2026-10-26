using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using KyberWeave.Core.Processes;

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
    private const string KyberDashBaseName = "kyberdash";

    /// <summary>
    /// First release whose <c>build-kyberdash</c> job succeeded and published
    /// <c>kyberdash-&lt;rid&gt;</c> assets; every earlier tag has none. An update that
    /// resolves an older release must not ask for the archive, because a missing asset
    /// fails the whole update — including the CLI replacement that would have worked.
    /// <c>KYBERDASH_MIN_VERSION</c> in <c>scripts/install.sh</c> is the same floor.
    /// </summary>
    private const string KyberDashMinVersion = "0.1.7-rc.9";

    /// <summary>
    /// First release whose <c>build-tray</c> job publishes the tray installers.
    /// </summary>
    /// <remarks>
    /// Confirmed against release v0.1.7-rc.13 where the <c>build-tray</c> job
    /// first published the macOS and Windows tray installers. The floor exists
    /// so an update resolving an older release does not delegate to a tray that
    /// release never carried.
    /// </remarks>
    private const string TrayMinVersion = "0.1.7-rc.13";

    /// <summary>Where <c>kyberdash menubar</c> records what it installed.</summary>
    private const string TrayRecordFile = "tray.json";

    private readonly GitHubReleaseClient _releases;
    private readonly SelfUpdateHost _host;
    private readonly Action<string> _log;
    private readonly Func<string, IReadOnlyList<string>, int> _runProcess;
    private readonly Func<string, string?> _readEnvironment;
    private readonly Func<string, bool> _canWriteDirectory;
    private readonly Func<string, IReadOnlyList<string>, ProcessResult> _captureProcess;

    /// <param name="captureProcess">
    /// Runs a KyberDash verb whose output decides the next step (the engine verdict, the plugin verdict).
    /// Separate from <paramref name="runProcess"/> because that seam returns only an exit code, and the restart
    /// line depends on what the verb printed. Tests pass a fake so no reconciliation starts a real process.
    /// </param>
    internal SelfUpdater(
        HttpMessageHandler handler,
        SelfUpdateHost host,
        Action<string>? log = null,
        Func<string, string?>? readEnvironment = null,
        Func<string, IReadOnlyList<string>, int>? runProcess = null,
        Func<string, bool>? canWriteDirectory = null,
        Func<string, IReadOnlyList<string>, ProcessResult>? captureProcess = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _log = log ?? (_ => { });
        _runProcess = runProcess ?? RunProcess;
        _captureProcess = captureProcess ?? RunCapture;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
        // A seam because the refusal cannot be provoked through mode bits everywhere:
        // root and Windows both write through a read-only POSIX mode.
        _canWriteDirectory = canWriteDirectory ?? CanWriteDirectory;
        _releases = new GitHubReleaseClient(
            handler,
            host.CurrentVersion,
            _readEnvironment);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient takes ownership of the handler (disposeHandler: true).")]
    internal static SelfUpdater Create(SelfUpdateHost host, Action<string>? log = null)
    {
        return new SelfUpdater(new SocketsHttpHandler { AllowAutoRedirect = true }, host, log);
    }

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
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                       or InvalidDataException)
        {
            return new SelfUpdateOutcome(1, ex.Message);
        }
    }

    public void Dispose()
    {
        _releases.Dispose();
    }

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

        bool windows = _host.IsWindows || PlatformRid.IsWindowsRid(_host.Rid);
        string current = ReleaseVersion.Normalize(_host.CurrentVersion);
        if (string.Equals(current, version, StringComparison.Ordinal))
        {
            // Nothing to download, but a new kyberdash can still be running an old engine, a tray, or an
            // OpenCode plugin file. Reconciling here is what clears a stale engine with one command (C3).
            ReconcileKyberDash(options, version, windows, false);
            return new SelfUpdateOutcome(0, $"already on {version}");
        }

        string tag = ReleaseVersion.Tag(version);
        DirectoryInfo work = Directory.CreateTempSubdirectory("kyber-weave-update-");
        try
        {
            _log($"installing {tag} ({_host.Rid}) → {_host.InstallDirectory}");
            string sums = _releases.DownloadChecksums(tag);
            List<(string BaseName, string ExtractedPath, string Destination)> staged =
            [
                StageBinary(CliBaseName, _host.Rid, tag, windows, sums, work.FullName)
            ];
            if (!options.NoMcp)
                staged.Add(StageBinary(McpBaseName, _host.Rid, tag, windows, sums, work.FullName));

            // KyberDash carries the Node SEA RID, not this host's .NET RID.
            if (ShouldUpdateKyberDash(options, version, windows))
            {
                staged.Add(StageBinary(
                    KyberDashBaseName,
                    PlatformRid.KyberDashRid(_host.Rid),
                    tag,
                    windows,
                    sums,
                    work.FullName));
            }

            // This process's own image is committed last. Overwriting it costs the runtime
            // the ability to load any assembly it has not already touched, because a
            // single-file host reads bundled assemblies back out of the executable by path.
            // Everything downstream — the remaining commits, and the rollback below — then
            // runs on borrowed code, so the swap belongs after the work that can still fail.
            staged =
            [
                .. staged.Where(item => !IsRunningImage(item.Destination)),
                .. staged.Where(item => IsRunningImage(item.Destination))
            ];

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
                        File.Copy(item.Destination, backupFile, true);
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
                                File.Copy(backup.BackupPath, backup.Destination, true);
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

        // After the binaries are in place, so every step that runs is the new kyberdash, and the tray
        // installer resolves the release this update just installed.
        ReconcileKyberDash(options, version, windows, true);

        string installed = Path.Combine(
            _host.InstallDirectory,
            BinaryInstaller.InstalledFileName(CliBaseName, windows));
        return new SelfUpdateOutcome(0, $"updated kyber-weave {version} → {installed}");
    }

    /// <summary>Whether to delegate to <c>kyberdash menubar --update</c>.</summary>
    /// <remarks>
    /// Mirrors <see cref="ShouldUpdateKyberDash"/>, and for the same reason:
    /// update replaces what is installed, it does not add a surface the user
    /// left out. Every skip is logged, because a silent one reads as the update
    /// having covered the tray when it did not.
    /// </remarks>
    private bool ShouldUpdateTray(SelfUpdateOptions options, string version, bool windows, bool releaseChanged)
    {
        if (options.NoMenubar)
        {
            _log("--no-menubar given; leaving the KyberDash tray unchanged");
            return false;
        }

        if (ReleaseVersion.Compare(version, TrayMinVersion) < 0)
        {
            _log(
                $"release {version} predates the KyberDash tray (first published in {TrayMinVersion}); leaving the tray unchanged");
            return false;
        }

        string record = Path.Combine(TrayConfigDirectory(), TrayRecordFile);
        if (!File.Exists(record))
        {
            _log($"no tray install recorded in {record}; install it with `kyberdash menubar`");
            return false;
        }

        // With no release change the binaries did not move, so the tray is relaunched only when the
        // tray that is running was recorded older than the kyberdash just reconciled.
        if (!releaseChanged)
        {
            string? recorded = ReadRecordedTrayVersion(record);
            if (recorded is null)
            {
                _log($"the tray record {record} has no readable version; leaving the KyberDash tray unchanged");
                return false;
            }

            if (ReleaseVersion.Compare(recorded, version) >= 0)
            {
                _log($"the KyberDash tray is already on {recorded}; leaving it unchanged");
                return false;
            }

            _log($"the KyberDash tray is on {recorded}, older than kyberdash {version}; relaunching it");
        }

        string kyberdash = Path.Combine(
            _host.InstallDirectory,
            BinaryInstaller.InstalledFileName(KyberDashBaseName, windows));
        if (!File.Exists(kyberdash))
        {
            _log($"kyberdash is not installed in {_host.InstallDirectory}; leaving the KyberDash tray unchanged");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Brings the running KyberDash processes in line with the installed kyberdash: the engine (D12, C2, C3), the
    /// tray (D12), then the OpenCode capture plugin (D13). Runs after the binaries commit on both the release-changed
    /// path and the "already on" path, so one command clears a stale engine.
    /// </summary>
    /// <remarks>
    /// A failed engine step throws a named failure, which the caller reports as exit 1. Nothing is rolled back: the
    /// binaries are already committed and are the new release. A failed plugin step is only a warning, because the
    /// plugin is an optional surface and the owner can rerun it with the printed command.
    /// </remarks>
    private void ReconcileKyberDash(SelfUpdateOptions options, string version, bool windows, bool releaseChanged)
    {
        if (options.NoKyberDash)
        {
            // Every step here is kyberdash itself. Running any of them would run whichever kyberdash is installed.
            _log("--no-kyberdash given; leaving the KyberDash engine, tray and capture plugin unchanged");
            return;
        }

        if (options.NoRestart)
        {
            _log("--no-restart given; leaving the KyberDash engine and tray running as they are");
        }
        else
        {
            EnsureEngine(windows);
            if (ShouldUpdateTray(options, version, windows, releaseChanged))
                UpdateTray(windows, version);
        }

        UpdateCapturePlugin(windows);
    }

    /// <summary>Asks the installed kyberdash to replace a running engine of another version. Starts nothing when none runs.</summary>
    private void EnsureEngine(bool windows)
    {
        string kyberdash = InstalledKyberDash(windows);
        if (!File.Exists(kyberdash))
        {
            _log($"kyberdash is not installed in {_host.InstallDirectory}; leaving the KyberDash engine unchanged");
            return;
        }

        _log("checking the KyberDash engine…");
        ProcessResult result;
        try
        {
            result = _captureProcess(kyberdash, ["engine", "ensure", "--if-running"]);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            throw new SelfUpdateException(
                $"the KyberDash engine step could not start `{kyberdash} engine ensure --if-running`: {ex.Message}. " +
                "The binaries are already updated; run `kyberdash engine ensure --if-running` to retry.");
        }

        if (result.ExitCode == 0)
        {
            _log(FirstLine(result.StandardOutput, "the KyberDash engine is checked"));
            return;
        }

        if (MentionsUnknownCommand(result))
        {
            _log("this KyberDash predates `engine ensure`; the running engine was not restarted");
            return;
        }

        throw new SelfUpdateException(
            $"the KyberDash engine step failed: `{kyberdash} engine ensure --if-running` exited {result.ExitCode}" +
            $"{Reason(result)}. The binaries are already updated; run `kyberdash engine ensure --if-running` to retry.");
    }

    /// <summary>
    /// Installs or upgrades the OpenCode capture plugin, but only when it is already installed (D13), so an update
    /// never adds a surface the owner did not choose. Prints the restart line when the plugin file changed.
    /// </summary>
    private void UpdateCapturePlugin(bool windows)
    {
        string kyberdash = InstalledKyberDash(windows);
        if (!File.Exists(kyberdash))
        {
            _log(
                $"kyberdash is not installed in {_host.InstallDirectory}; leaving the OpenCode capture plugin unchanged");
            return;
        }

        ProcessResult result;
        try
        {
            result = _captureProcess(
                kyberdash,
                ["kyber", "capture", "plugin", "install", "--harness", "opencode", "--preserve", "--if-installed"]);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            _log($"warning: the OpenCode capture plugin step could not run KyberDash: {ex.Message}");
            return;
        }

        if (result.ExitCode != 0)
        {
            if (MentionsUnknownCommand(result))
                _log("this KyberDash predates the capture plugin command; the OpenCode capture plugin was not updated");
            else
                _log(
                    $"warning: the OpenCode capture plugin step failed (exit {result.ExitCode}){Reason(result)}. " +
                    "The update is otherwise complete. Retry with " +
                    "`kyberdash kyber capture plugin install --harness opencode --preserve --if-installed`.");
            return;
        }

        string verdict = FirstLine(result.StandardOutput, "the OpenCode capture plugin is checked");
        _log(verdict);
        if (PluginChanged(verdict))
            _log("Restart OpenCode to load the upgraded capture plugin.");
    }

    /// <summary>The installed kyberdash, beside kyber-weave. Reconciliation never looks on PATH, so it only touches the binary this update installed.</summary>
    private string InstalledKyberDash(bool windows)
    {
        return Path.Combine(_host.InstallDirectory, BinaryInstaller.InstalledFileName(KyberDashBaseName, windows));
    }

    /// <summary>The version the tray record names, or null when the record cannot be read or names no valid version.</summary>
    private static string? ReadRecordedTrayVersion(string record)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(record));
            if (document.RootElement.TryGetProperty("version", out JsonElement version)
                && version.ValueKind == JsonValueKind.String)
                return ReleaseVersion.Normalize(version.GetString() ?? string.Empty);
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
                                       or SelfUpdateException)
        {
            return null;
        }
    }

    private static bool PluginChanged(string verdict)
    {
        if (!verdict.StartsWith("opencode plugin: ", StringComparison.Ordinal))
            return false;

        string[] fields = verdict.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3 && fields[2] is "installed" or "upgraded";
    }

    private static bool MentionsUnknownCommand(ProcessResult result)
    {
        return (result.StandardOutput + "\n" + result.StandardError).Contains("unknown command",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string text, string fallback)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }

        return fallback;
    }

    /// <summary>Names the first line of stderr (or stdout) as the reason, when the process printed one.</summary>
    private static string Reason(ProcessResult result)
    {
        string text = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        string first = FirstLine(text, string.Empty);
        return first.Length == 0 ? string.Empty : $": {first}";
    }

    /// <summary>Runs a verb and captures what it printed. A process that cannot start surfaces as the caller's named failure.</summary>
    private static ProcessResult RunCapture(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        return ProcessRunner.Run(start, string.Empty);
    }

    private void UpdateTray(bool windows, string version)
    {
        string kyberdash = Path.Combine(
            _host.InstallDirectory,
            BinaryInstaller.InstalledFileName(KyberDashBaseName, windows));

        _log("updating the KyberDash tray…");
        int exitCode;
        try
        {
            exitCode = _runProcess(kyberdash, ["menubar", "--update", "--version", version]);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException
                                       or UnauthorizedAccessException)
        {
            // Same named failure as a non-zero exit: the binaries are committed and nothing is rolled back.
            throw new SelfUpdateException(
                $"the KyberDash tray step could not start `{kyberdash} menubar --update --version {version}`: {ex.Message}.");
        }

        if (exitCode != 0)
        {
            // Named, so the user can tell a tray failure from a CLI one. The
            // binaries are already committed, so this does not roll anything
            // back — it reports that one step of the update did not finish.
            throw new SelfUpdateException(
                $"the KyberDash tray step failed: `{kyberdash} menubar --update --version {version}` exited {exitCode}.");
        }

        _log("updated the KyberDash tray");
    }

    /// <summary>
    /// <c>~/.kyberdash</c>, where the CLI writes its own state (R3.5).
    /// </summary>
    /// <remarks>
    /// Read through the injected environment reader rather than
    /// <c>SpecialFolder.UserProfile</c>, so a test asserting the "no tray.json"
    /// skip does not depend on whether the developer running it happens to have
    /// a tray installed.
    /// </remarks>
    private string TrayConfigDirectory()
    {
        string home =
            _readEnvironment("HOME")
            ?? _readEnvironment("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".kyberdash");
    }

    private static int RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(fileName) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process? process = Process.Start(start);
        if (process is null)
            throw new SelfUpdateException($"the KyberDash tray step could not start {fileName}.");

        process.WaitForExit();
        return process.ExitCode;
    }

    private (string BaseName, string ExtractedPath, string Destination) StageBinary(
        string baseName,
        string rid,
        string tag,
        bool windows,
        string sums,
        string workDirectory)
    {
        string archiveName = BinaryInstaller.ArchiveName(baseName, rid);
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
        BinaryInstaller.Replace(extractedPath, destination, windows, _host.IsMacOs);
        _log($"installed {baseName} {ReleaseVersion.Normalize(tag)} → {destination}");
    }

    /// <summary>Decides whether this update should replace an installed KyberDash.</summary>
    /// <remarks>
    /// Update replaces what is installed; it does not add a binary the user left out.
    /// <c>install.sh --no-kyberdash</c> is an explicit opt-out, and a machine that installed
    /// before KyberDash existed never chose to run it either — so an absent binary is reported
    /// rather than silently created. Both skips are logged, because a silent one reads as the
    /// update having covered KyberDash when it did not.
    /// </remarks>
    private bool ShouldUpdateKyberDash(SelfUpdateOptions options, string version, bool windows)
    {
        if (options.NoKyberDash)
            return false;

        if (ReleaseVersion.Compare(version, KyberDashMinVersion) < 0)
        {
            _log(
                $"release {version} predates KyberDash (first published in {KyberDashMinVersion}); leaving kyberdash unchanged");
            return false;
        }

        string installed = Path.Combine(
            _host.InstallDirectory,
            BinaryInstaller.InstalledFileName(KyberDashBaseName, windows));
        if (!File.Exists(installed))
        {
            _log($"kyberdash is not installed in {_host.InstallDirectory}; add it with scripts/install.sh");
            return false;
        }

        return true;
    }

    private bool IsRunningImage(string destination)
    {
        if (string.IsNullOrWhiteSpace(_host.ProcessPath))
            return false;

        return string.Equals(
            Path.GetFullPath(destination),
            Path.GetFullPath(_host.ProcessPath),
            _host.IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
        if (!Directory.Exists(directory) || !_canWriteDirectory(directory))
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
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                       FileOptions.DeleteOnClose))
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
