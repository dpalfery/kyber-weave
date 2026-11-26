using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Core.Processes;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Installs, updates, and launches the KyberDash tray application on macOS and Windows.
/// </summary>
/// <remarks>
/// The tray ships for macOS and Windows only, so other platforms get a refusal rather than a
/// half-installed directory. A download is verified twice before it is unpacked: its SHA-256 must
/// match the release checksum list, and on macOS the bundle's code signature team must match the
/// expected one. Any failure leaves the previously installed tray untouched.
/// </remarks>
public partial class MenubarService : IMenubarService
{
    /// <summary>Default release host serving tray artifacts.</summary>
    public const string DefaultReleaseHost = "https://github.com/dpalfery/kyber-weave/releases/download";

    /// <summary>Environment variable overriding the release host (offline testing and mirrors).</summary>
    public const string ReleaseOriginEnvironmentVariable = "KYBER_WEAVE_RELEASE_ORIGIN";

    /// <summary>Name of the checksum list published alongside each release.</summary>
    public const string ChecksumFile = "SHA256SUMS.txt";

    /// <summary>Apple developer team identifier the tray bundle must carry.</summary>
    public const string ExpectedTeamId = "J2UNNQ466J";

    /// <summary>Record file naming the installed tray inside the state directory.</summary>
    public const string TrayRecordFile = "tray.json";

    /// <summary>Bundle name of the macOS tray application.</summary>
    public const string MacosAppName = "KyberDash.app";

    private const string TrayProcessPattern = "Contents/MacOS/kyberdash-tray";
    private const string LegacyMenubarProcessPattern = "KyberDashMenubar.app/";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrayPollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>How long a force-killed tray may take to leave the process table before the kill counts as failed.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromMilliseconds(750);
    private const string WindowsTrayImage = "kyberdash-tray.exe";
    private const string PlatformRefusal = "kyberdash menubar: platform: the KyberDash tray ships for macOS and Windows only; see docs/todo for the Linux tray";
    private const string NotRunningMessage = "kyberdash: the tray is not running";
    private const string NoRecordMessage = "kyberdash: no tray install recorded";
    private const string QuitDoneMessage = "kyberdash: the tray quit";

    private static readonly JsonSerializerOptions RecordJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HttpClient DownloadClient = new(new SocketsHttpHandler { UseProxy = true }, disposeHandler: true)
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    private readonly IServerStateRepository serverState;
    private readonly TrayToolRunner runTool;
    private readonly TrayPlatform platform;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action<string> startDetached;

    /// <summary>
    /// Initializes a new <see cref="MenubarService"/> that runs the real system tools on the real platform.
    /// </summary>
    /// <param name="serverState">Server-state repository supplying the tray install directory.</param>
    public MenubarService(IServerStateRepository serverState)
        : this(serverState, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a new <see cref="MenubarService"/> with its outside resources supplied, so the quit, launch and
    /// running decisions are testable without a tray, a platform or a clock.
    /// </summary>
    /// <param name="serverState">Server-state repository supplying the tray install directory.</param>
    /// <param name="runTool">Runs an external tool; the system process runner when <see langword="null"/>.</param>
    /// <param name="platform">The platform to decide for; the running one when <see langword="null"/>.</param>
    /// <param name="delay">Waits between quit polls; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when <see langword="null"/>.</param>
    /// <param name="startDetached">Starts the Windows tray executable without waiting for it; a real process start when <see langword="null"/>.</param>
    public MenubarService(
        IServerStateRepository serverState,
        TrayToolRunner? runTool,
        TrayPlatform? platform,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Action<string>? startDetached = null)
    {
        ArgumentNullException.ThrowIfNull(serverState);
        this.serverState = serverState;
        this.runTool = runTool ?? RunToolAsync;
        this.platform = platform ?? CurrentPlatform();
        this.delay = delay ?? Task.Delay;
        this.startDetached = startDetached ?? StartProcessDetached;
    }

    private static void StartProcessDetached(string path)
    {
        using Process? started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false });
    }

    private static TrayPlatform CurrentPlatform() =>
        OperatingSystem.IsMacOS() ? TrayPlatform.MacOS
        : OperatingSystem.IsWindows() ? TrayPlatform.Windows
        : TrayPlatform.Other;

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(.+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ChecksumLineRegex();

    [GeneratedRegex(@"TeamIdentifier=(\S+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TeamIdentifierRegex();

    /// <inheritdoc />
    public async Task<MenubarInstallOutcome> InstallOrUpdateAsync(
        bool force,
        bool update,
        string? requestedVersion,
        string cliVersion,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            return new MenubarInstallOutcome(
                1,
                "kyberdash menubar: platform: the KyberDash tray ships for macOS and Windows only; see docs/todo for the Linux tray");
        }

        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
        {
            return new MenubarInstallOutcome(
                1,
                "kyberdash menubar: platform: the KyberDash tray ships for macOS and Windows only.");
        }

        string configDir = serverState.DefaultStateDirectory;
        string recordPath = Path.Combine(configDir, TrayRecordFile);
        TrayRecord? existing = ReadTrayRecord(recordPath);

        if (update && existing is null)
        {
            return new MenubarInstallOutcome(0, "kyberdash: no tray install recorded; nothing to update");
        }

        if (existing is not null && !force && !update)
        {
            return new MenubarInstallOutcome(0, string.Create(
                CultureInfo.InvariantCulture,
                $"kyberdash: the tray is already installed at {existing.Path}"));
        }

        string version = string.IsNullOrWhiteSpace(requestedVersion) ? cliVersion : requestedVersion.Trim();
        string origin = ResolveReleaseOrigin(version);
        string artifact = TrayArtifactName();

        byte[] payload;
        try
        {
            payload = await DownloadAndVerifyAsync(origin, artifact, cancellationToken).ConfigureAwait(false);
        }
        catch (TrayInstallException ex)
        {
            return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: {ex.Step}: {ex.Message}"));
        }

        string staging = Directory.CreateTempSubdirectory("kyberdash-tray-").FullName;
        try
        {
            string installedPath = OperatingSystem.IsMacOS()
                ? await InstallMacosAsync(payload, staging, artifact, configDir, recordPath, version, existing, cancellationToken).ConfigureAwait(false)
                : await InstallWindowsAsync(payload, staging, artifact, configDir, recordPath, version, existing, cancellationToken).ConfigureAwait(false);

            return new MenubarInstallOutcome(0, string.Create(
                CultureInfo.InvariantCulture,
                $"kyberdash: tray {version} installed at {installedPath}"));
        }
        catch (TrayInstallException ex)
        {
            return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: {ex.Step}: {ex.Message}"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is harmless; the next install re-creates its own.
            }
        }
    }

    /// <summary>Artifact file name the current platform installs.</summary>
    /// <returns>The release artifact name.</returns>
    public static string TrayArtifactName() =>
        OperatingSystem.IsMacOS()
            ? "kyberdash-tray-darwin-" + ArchitectureLabel() + ".zip"
            : "kyberdash-tray-win-x64-setup.exe";

    /// <summary>Resolves the release origin a version's artifacts sit under.</summary>
    /// <param name="version">Version being installed.</param>
    /// <returns>Base URL without a trailing slash.</returns>
    public static string ResolveReleaseOrigin(string version)
    {
        string? environment = Environment.GetEnvironmentVariable(ReleaseOriginEnvironmentVariable)?.Trim();
        if (!string.IsNullOrEmpty(environment))
        {
            return environment.TrimEnd('/');
        }

        return string.Create(CultureInfo.InvariantCulture, $"{DefaultReleaseHost}/v{version}");
    }

    /// <summary>Reads the recorded tray install, or <see langword="null"/> when none is recorded.</summary>
    /// <param name="recordPath">Path of <c>tray.json</c>.</param>
    /// <returns>The record, or <see langword="null"/>.</returns>
    public static TrayRecord? ReadTrayRecord(string recordPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);
        if (!File.Exists(recordPath))
        {
            return null;
        }

        try
        {
            TrayRecord? parsed = JsonSerializer.Deserialize<TrayRecord>(File.ReadAllText(recordPath, Encoding.UTF8), RecordJsonOptions);
            return parsed is not null && !string.IsNullOrWhiteSpace(parsed.Path) ? parsed : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable record is treated as "nothing installed", so a reinstall still works.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<MenubarInstallOutcome> QuitAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupportedPlatform())
        {
            return new MenubarInstallOutcome(1, PlatformRefusal);
        }

        TrayRecord? record = ReadRecordOrNull();
        if (record is null)
        {
            return new MenubarInstallOutcome(1, NoRecordMessage);
        }

        // Only the binary recorded at install is ever signalled, so a developer's own tray is never touched.
        string binary = TrayBinaryPath(record);
        string pattern = AnchoredCommandLinePattern(binary);
        if (!File.Exists(binary))
        {
            return new MenubarInstallOutcome(0, NotRunningMessage);
        }

        TrayProbe first = await ProbeAsync(pattern, cancellationToken).ConfigureAwait(false);
        if (first.Failure is not null)
        {
            return ProbeFailure(first.Failure);
        }

        if (first.Count == 0)
        {
            return new MenubarInstallOutcome(0, NotRunningMessage);
        }

        try
        {
            _ = await runTool(binary, ["--quit"], QuitTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The wait below decides whether the tray exited; a slow quit is not a failure by itself.
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // The tool never ran, so the tray was not asked to quit and a force-kill is not warranted.
            return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: quit: {exception.Message}"));
        }

        string? probeFailure = null;
        async Task<bool> StillRunningAsync(CancellationToken c)
        {
            TrayProbe probe = await ProbeAsync(pattern, c).ConfigureAwait(false);
            probeFailure = probe.Failure;
            return probe.Failure is null && probe.Count > 0;
        }

        bool exited = await TrayProcesses.WaitForTrayExitAsync(
            StillRunningAsync,
            delay,
            TrayPollInterval,
            QuitTimeout,
            cancellationToken).ConfigureAwait(false);
        if (probeFailure is not null)
        {
            return ProbeFailure(probeFailure);
        }

        if (exited)
        {
            return new MenubarInstallOutcome(0, QuitDoneMessage);
        }

        if (platform == TrayPlatform.Windows)
        {
            // Windows image names do not identify one install, so a force-kill could stop another tray.
            return new MenubarInstallOutcome(1, "kyberdash: the tray is still running after quit; quit it and run again");
        }

        ProcessResult kill;
        try
        {
            kill = await runTool("/usr/bin/pkill", ["-f", pattern], QuitTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or TimeoutException)
        {
            return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: quit: kill: {exception.Message}"));
        }

        // pkill exits 0 when it signalled something and 1 when nothing matched (the tray left on its own, which
        // the grace wait below confirms); any other code means pkill itself failed.
        if (kill.ExitCode is not (0 or 1))
        {
            return new MenubarInstallOutcome(
                1,
                FirstNonEmpty(kill.StandardError, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: quit: kill: pkill exited {kill.ExitCode}")));
        }

        bool gone = await TrayProcesses.WaitForTrayExitAsync(
            StillRunningAsync,
            delay,
            TrayPollInterval,
            KillGrace,
            cancellationToken).ConfigureAwait(false);
        if (probeFailure is not null)
        {
            return ProbeFailure(probeFailure);
        }

        if (!gone)
        {
            return new MenubarInstallOutcome(1, "kyberdash: the tray is still running after quit and kill; quit it and run again");
        }

        return new MenubarInstallOutcome(0, QuitDoneMessage);
    }

    /// <inheritdoc />
    public async Task<MenubarInstallOutcome> LaunchAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupportedPlatform())
        {
            return new MenubarInstallOutcome(1, PlatformRefusal);
        }

        TrayRecord? record = ReadRecordOrNull();
        if (record is null)
        {
            return new MenubarInstallOutcome(1, NoRecordMessage);
        }

        if (platform == TrayPlatform.MacOS)
        {
            if (!Directory.Exists(record.Path))
            {
                return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash: the tray bundle is missing at {record.Path}"));
            }

            ProcessResult open;
            try
            {
                open = await runTool("/usr/bin/open", [record.Path], QuitTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or TimeoutException)
            {
                return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: launch: {exception.Message}"));
            }

            return open.ExitCode == 0
                ? new MenubarInstallOutcome(0, "kyberdash: the tray launched")
                : new MenubarInstallOutcome(1, FirstNonEmpty(open.StandardError, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: launch: open exited {open.ExitCode}")));
        }

        if (!File.Exists(record.Path))
        {
            return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash: the tray is missing at {record.Path}"));
        }

        try
        {
            startDetached(record.Path);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new MenubarInstallOutcome(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: launch: {ex.Message}"));
        }

        return new MenubarInstallOutcome(0, "kyberdash: the tray launched");
    }

    /// <inheritdoc />
    public async Task<MenubarInstallOutcome> CountRunningAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupportedPlatform())
        {
            return new MenubarInstallOutcome(1, PlatformRefusal);
        }

        // With a record, count only the recorded binary, as quit does; a lookalike path is not "the tray".
        // Without one nothing was installed here, so the generic bundle path is the best available match.
        TrayRecord? record = ReadRecordOrNull();
        string pattern = record is null ? TrayProcessPattern : AnchoredCommandLinePattern(TrayBinaryPath(record));
        TrayProbe probe = await ProbeAsync(pattern, cancellationToken).ConfigureAwait(false);
        return probe.Failure is null ? TrayProcesses.RunningOutcome(probe.Count) : ProbeFailure(probe.Failure);
    }

    private bool IsSupportedPlatform() => platform != TrayPlatform.Other;

    private TrayRecord? ReadRecordOrNull() =>
        ReadTrayRecord(Path.Combine(serverState.DefaultStateDirectory, TrayRecordFile));

    private string TrayBinaryPath(TrayRecord record) =>
        platform == TrayPlatform.MacOS ? Path.Combine(record.Path, "Contents", "MacOS", "kyberdash-tray") : record.Path;

    /// <summary>
    /// Builds the <c>pgrep -f</c> / <c>pkill -f</c> pattern for exactly one binary: anchored to the start of the
    /// command line and followed by a space or the end, so another process whose arguments merely contain the
    /// path (an editor, a <c>tail</c> of a log) is neither counted nor signalled.
    /// </summary>
    private static string AnchoredCommandLinePattern(string binary)
    {
        StringBuilder escaped = new("^");
        foreach (char character in binary)
        {
            if ("\\.[]{}()*+?^$|".Contains(character, StringComparison.Ordinal))
            {
                _ = escaped.Append('\\');
            }

            _ = escaped.Append(character);
        }

        return escaped.Append("( |$)").ToString();
    }

    private sealed record TrayProbe(int Count, string? Failure);

    private static MenubarInstallOutcome ProbeFailure(string failure) =>
        new(1, string.Create(CultureInfo.InvariantCulture, $"kyberdash menubar: could not probe for the tray: {failure}"));

    /// <summary>Counts the tray processes whose command line matches <paramref name="pgrepPattern"/> (macOS) or by image name (Windows).</summary>
    /// <remarks>
    /// <c>pgrep</c> exits 0 for a match, 1 for none and anything else for a failure of the probe itself;
    /// <c>tasklist</c> exits 0 either way. Gating on the exit code keeps a broken probe from reading as
    /// "no tray" (which would skip a needed quit) or as a running tray.
    /// </remarks>
    private async Task<TrayProbe> ProbeAsync(string pgrepPattern, CancellationToken cancellationToken)
    {
        bool windows = platform == TrayPlatform.Windows;
        string tool = windows ? "tasklist" : "/usr/bin/pgrep";
        string[] arguments = windows
            ? ["/FI", "IMAGENAME eq " + WindowsTrayImage, "/FO", "CSV", "/NH"]
            : ["-f", pgrepPattern];
        ProcessResult listing;
        try
        {
            listing = await runTool(tool, arguments, QuitTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or TimeoutException)
        {
            return new TrayProbe(0, string.Create(CultureInfo.InvariantCulture, $"{tool}: {exception.Message}"));
        }

        if (!windows && listing.ExitCode == 1)
        {
            return new TrayProbe(0, null);
        }

        if (listing.ExitCode != 0)
        {
            string detail = FirstNonEmpty(listing.StandardError, string.Create(CultureInfo.InvariantCulture, $"exited {listing.ExitCode}"));
            return new TrayProbe(0, string.Create(CultureInfo.InvariantCulture, $"{tool}: {detail}"));
        }

        return new TrayProbe(TrayProcesses.CountTrayProcesses(listing.StandardOutput, windows), null);
    }

    private static async Task<bool> PatternRunningAsync(string pattern, CancellationToken cancellationToken)
    {
        ProcessResult pgrep = await RunToolAsync("/usr/bin/pgrep", ["-f", pattern], QuitTimeout, cancellationToken).ConfigureAwait(false);
        return pgrep.ExitCode == 0;
    }

    private static async Task<byte[]> DownloadAndVerifyAsync(string origin, string artifact, CancellationToken cancellationToken)
    {
        string checksumsUrl = string.Create(CultureInfo.InvariantCulture, $"{origin}/{Uri.EscapeDataString(ChecksumFile)}");
        string artifactUrl = string.Create(CultureInfo.InvariantCulture, $"{origin}/{Uri.EscapeDataString(artifact)}");

        string checksums;
        try
        {
            checksums = await DownloadClient.GetStringAsync(new Uri(checksumsUrl), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new TrayInstallException(
                "checksums",
                string.Create(CultureInfo.InvariantCulture, $"could not fetch {checksumsUrl}: {exception.Message}"),
                exception);
        }

        string? expected = FindExpectedChecksum(checksums, artifact);
        if (expected is null)
        {
            throw new TrayInstallException(
                "checksums",
                string.Create(CultureInfo.InvariantCulture, $"{artifact} is not listed in {ChecksumFile}"));
        }

        byte[] payload;
        try
        {
            payload = await DownloadClient.GetByteArrayAsync(new Uri(artifactUrl), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new TrayInstallException(
                "download",
                string.Create(CultureInfo.InvariantCulture, $"could not fetch {artifactUrl}: {exception.Message}"),
                exception);
        }

        string actual = Convert.ToHexString(SHA256.HashData(payload));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrayInstallException(
                "checksum",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"SHA-256 mismatch for {artifact}: expected {expected.ToUpperInvariant()}, got {actual}"));
        }

        return payload;
    }

    /// <summary>Finds the published checksum for <paramref name="artifact"/> in a <c>SHA256SUMS</c> body.</summary>
    /// <param name="checksums">Checksum file contents.</param>
    /// <param name="artifact">Artifact file name.</param>
    /// <returns>The lowercase hex digest, or <see langword="null"/> when absent.</returns>
    public static string? FindExpectedChecksum(string checksums, string artifact)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact);

        foreach (string line in checksums.Split('\n'))
        {
            Match match = ChecksumLineRegex().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            string name = match.Groups[2].Value.Trim();
            string baseName = name.Split(['/', '\\'])[^1];
            if (string.Equals(baseName, artifact, StringComparison.Ordinal))
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static async Task<string> InstallMacosAsync(
        byte[] payload,
        string staging,
        string artifact,
        string configDir,
        string recordPath,
        string version,
        TrayRecord? existing,
        CancellationToken cancellationToken)
    {
        string archive = Path.Combine(staging, artifact);
        await File.WriteAllBytesAsync(archive, payload, cancellationToken).ConfigureAwait(false);

        ProcessResult unzip = await RunToolAsync("/usr/bin/ditto", ["-x", "-k", archive, staging], ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (unzip.ExitCode != 0)
        {
            throw new TrayInstallException("unpack", FirstNonEmpty(unzip.StandardError, string.Create(CultureInfo.InvariantCulture, $"ditto exited {unzip.ExitCode}")));
        }

        string stagedApp = Path.Combine(staging, MacosAppName);
        if (!Directory.Exists(stagedApp))
        {
            throw new TrayInstallException("unpack", string.Create(CultureInfo.InvariantCulture, $"expected {MacosAppName} in unpacked archive"));
        }

        string teamId = await VerifyCodeSignatureAsync(stagedApp, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(teamId, ExpectedTeamId, StringComparison.Ordinal))
        {
            throw new TrayInstallException(
                "team-id",
                string.Create(CultureInfo.InvariantCulture, $"expected {ExpectedTeamId}, got {teamId}"));
        }

        ProcessResult assess = await RunToolAsync(
            "/usr/sbin/spctl",
            ["--assess", "--type", "execute", "--verbose", stagedApp],
            ToolTimeout,
            cancellationToken).ConfigureAwait(false);
        if (assess.ExitCode != 0)
        {
            throw new TrayInstallException("spctl", FirstNonEmpty(assess.StandardError, string.Create(CultureInfo.InvariantCulture, $"spctl exited {assess.ExitCode}")));
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string applicationsDir = IsDirectoryWritable("/Applications")
            ? "/Applications"
            : EnsureDirectory(Path.Combine(home, "Applications"));
        string target = Path.Combine(applicationsDir, MacosAppName);

        await QuitRunningTrayAsync(target, existing, home, cancellationToken).ConfigureAwait(false);

        string? trashed = null;
        try
        {
            if (Directory.Exists(target) || File.Exists(target))
            {
                trashed = MoveToTrash(target, home);
            }

            Directory.Move(stagedApp, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string suffix = trashed is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" (the previous app is in the Trash at {trashed})");
            throw new TrayInstallException("install", $"{ex.Message}{suffix}", ex);
        }

        WriteRecord(configDir, recordPath, target, version, "darwin", existing);

        ProcessResult open = await RunToolAsync("/usr/bin/open", [target], QuitTimeout, cancellationToken).ConfigureAwait(false);
        if (open.ExitCode != 0)
        {
            throw new TrayInstallException("launch", FirstNonEmpty(open.StandardError, string.Create(CultureInfo.InvariantCulture, $"open exited {open.ExitCode}")));
        }

        return target;
    }

    private static async Task<string> InstallWindowsAsync(
        byte[] payload,
        string staging,
        string artifact,
        string configDir,
        string recordPath,
        string version,
        TrayRecord? existing,
        CancellationToken cancellationToken)
    {
        string installer = Path.Combine(staging, artifact);
        await File.WriteAllBytesAsync(installer, payload, cancellationToken).ConfigureAwait(false);

        ProcessResult run = await RunToolAsync(installer, ["/S"], ToolTimeout, cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
        {
            throw new TrayInstallException("install", string.Create(CultureInfo.InvariantCulture, $"the installer exited {run.ExitCode}"));
        }

        string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string target = Path.Combine(localAppData, "KyberDash", "kyberdash-tray.exe");
        WriteRecord(configDir, recordPath, target, version, "win32", existing);

        ProcessResult launch = await RunToolAsync(target, [], QuitTimeout, cancellationToken).ConfigureAwait(false);
        if (launch.ExitCode != 0)
        {
            throw new TrayInstallException("launch", string.Create(CultureInfo.InvariantCulture, $"tray exited {launch.ExitCode}"));
        }

        return target;
    }

    private static async Task<string> VerifyCodeSignatureAsync(string appBundle, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunToolAsync(
            "/usr/bin/codesign",
            ["--verify", "--deep", "--strict", appBundle],
            ToolTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new TrayInstallException("codesign", FirstNonEmpty(result.StandardError, string.Create(CultureInfo.InvariantCulture, $"codesign exited {result.ExitCode}")));
        }

        ProcessResult details = await RunToolAsync(
            "/usr/bin/codesign",
            ["--display", "--verbose=4", appBundle],
            ToolTimeout,
            cancellationToken).ConfigureAwait(false);
        Match match = TeamIdentifierRegex().Match(details.StandardError + "\n" + details.StandardOutput);
        if (!match.Success)
        {
            return "none";
        }

        string value = match.Groups[1].Value;
        return value is "not" or "not set" ? "none" : value;
    }

    private static async Task QuitRunningTrayAsync(
        string target,
        TrayRecord? existing,
        string home,
        CancellationToken cancellationToken)
    {
        HashSet<string> candidates = new(StringComparer.Ordinal) { target };
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.Path))
        {
            candidates.Add(existing.Path);
        }

        candidates.Add(Path.Combine("/Applications", MacosAppName));
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(Path.Combine(home, "Applications", MacosAppName));
        }

        foreach (string copy in candidates)
        {
            string bin = Path.Combine(copy, "Contents", "MacOS", "kyberdash-tray");
            if (!File.Exists(bin))
            {
                continue;
            }

            try
            {
                _ = await RunToolAsync(bin, ["--quit"], QuitTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Best-effort graceful quit before falling back to pkill below.
            }
            catch (Win32Exception)
            {
                // Executable vanished or is unrunnable; pkill fallback handles leftover processes.
            }
        }

        foreach (string pattern in new[] { TrayProcessPattern, LegacyMenubarProcessPattern })
        {
            if (!await PatternRunningAsync(pattern, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            // Wait for a tray that is already quitting; kill it only if it is still there after the timeout.
            bool exited = await TrayProcesses.WaitForTrayExitAsync(
                c => PatternRunningAsync(pattern, c),
                (span, c) => Task.Delay(span, c),
                TrayPollInterval,
                QuitTimeout,
                cancellationToken).ConfigureAwait(false);
            if (exited)
            {
                continue;
            }

            _ = await RunToolAsync("/usr/bin/pkill", ["-f", pattern], QuitTimeout, cancellationToken).ConfigureAwait(false);
            if (await PatternRunningAsync(pattern, cancellationToken).ConfigureAwait(false))
            {
                throw new TrayInstallException(
                    "quit",
                    string.Create(CultureInfo.InvariantCulture, $"{pattern} would not stop; quit it and run the update again"));
            }
        }
    }

    private static string MoveToTrash(string path, string home)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new IOException("no home directory to find the Trash in");
        }

        string trashDir = EnsureDirectory(Path.Combine(home, ".Trash"));
        string baseName = Path.GetFileName(path);
        string dest = Path.Combine(trashDir, string.Create(CultureInfo.InvariantCulture, $"{baseName}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
        Directory.Move(path, dest);
        return dest;
    }

    private static bool IsDirectoryWritable(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        string probe = Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $".kyberdash-write-probe-{Environment.ProcessId}"));
        try
        {
            using FileStream fs = new(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Close();
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string EnsureDirectory(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FirstNonEmpty(string text, string fallback)
    {
        string trimmed = text.Trim();
        return trimmed.Length > 0 ? trimmed : fallback;
    }

    private static void WriteRecord(
        string configDir,
        string recordPath,
        string target,
        string version,
        string platform,
        TrayRecord? existing)
    {
        Directory.CreateDirectory(configDir);
        string? exePath = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Environment.ProcessPath
            : existing?.KyberDashPath;

        TrayRecord record = new(
            Path: target,
            Version: version,
            InstalledAt: DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            Platform: platform,
            KyberDashPath: exePath);

        string temporary = recordPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, RecordJsonOptions) + "\n", Encoding.UTF8);
        File.Move(temporary, recordPath, overwrite: true);
    }

    private static string ArchitectureLabel() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static async Task<ProcessResult> RunToolAsync(
        string fileName,
        string[] arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return await Task.Run(
            () =>
            {
                try
                {
                    return ProcessRunner.Run(
                        new ProcessStartInfo(fileName)
                        {
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }.WithArguments(arguments),
                        standardInput: string.Empty,
                        timeout: timeout,
                        cancellationToken: cancellationToken);
                }
                catch (Win32Exception ex)
                {
                    return new ProcessResult(127, string.Empty, ex.Message);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Clones a start info with its positional arguments replaced.</summary>
internal static class ProcessStartInfoArguments
{
    /// <summary>Returns <paramref name="startInfo"/> with <paramref name="arguments"/> appended.</summary>
    /// <param name="startInfo">Start info to clone.</param>
    /// <param name="arguments">Positional arguments.</param>
    /// <returns>The cloned, populated start info.</returns>
    public static ProcessStartInfo WithArguments(this ProcessStartInfo startInfo, IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(arguments);

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

/// <summary>One recorded tray installation.</summary>
/// <param name="Path">Absolute path of the installed application or executable.</param>
/// <param name="Version">Installed version.</param>
/// <param name="InstalledAt">ISO-8601 UTC install timestamp.</param>
/// <param name="Platform">Platform identifier (<c>darwin</c> or <c>win32</c>).</param>
/// <param name="KyberDashPath">Path of the <c>kyberdash</c> CLI the tray launches, when known.</param>
public record TrayRecord(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("installedAt")] string InstalledAt,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("kyberdashPath")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? KyberDashPath);
