using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Update;

/// <summary>The processes, files and archives the self-update touches: starts children, probes the install directory and replaces binaries.</summary>
/// <remarks>The bodies are the self-updater's own, moved without change, so the bounds and the Windows paths behave as before.</remarks>
internal sealed class SelfUpdateSystem : ISelfUpdateSystem
{
    /// <summary>
    /// The bound for a KyberDash verb whose output decides the next step (engine ensure, plugin install). It may
    /// start or stop an engine, so it gets two minutes; a verb that outlasts that is stopped and named.
    /// </summary>
    private static readonly TimeSpan CaptureVerbTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The bound for the tray update. It quits, replaces and relaunches the tray, so it is the longest step.
    /// </summary>
    private static readonly TimeSpan TrayUpdateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The bound for the finish-update child the old process starts after every binary is committed. The child may run
    /// the engine verb (120 s), the tray (5 min) and two plugin steps (120 s each), so 15 minutes is longer than all of
    /// them together and a hung child is still stopped.
    /// </summary>
    private static readonly TimeSpan FinishUpdateTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The bound for the pre-swap rehearsal, which runs the installed command with <c>--version</c> and must return at once.</summary>
    private static readonly TimeSpan RehearsalTimeout = TimeSpan.FromSeconds(30);


    /// <inheritdoc />
    public int RunProcess(string fileName, IReadOnlyList<string> arguments) => RunProcessCore(fileName, arguments);

    /// <inheritdoc />
    public ProcessResult RunCapture(string fileName, IReadOnlyList<string> arguments) => RunCaptureCore(fileName, arguments);

    /// <inheritdoc />
    public int StartFinishingProcess(string fileName, IReadOnlyList<string> arguments, bool discardOutput) =>
        StartFinishingProcess(
            fileName,
            arguments,
            discardOutput,
            discardOutput ? RehearsalTimeout : FinishUpdateTimeout);

    /// <inheritdoc />
    bool ISelfUpdateSystem.CanWriteDirectory(string directory) => CanWriteDirectory(directory);

    /// <inheritdoc />
    void ISelfUpdateSystem.ExtractArchive(string archivePath, string destinationDirectory, bool windows) =>
        ExtractArchiveCore(archivePath, destinationDirectory, windows);

    /// <inheritdoc />
    void ISelfUpdateSystem.ReplaceBinary(string extractedBinaryPath, string destinationPath, bool windows, bool macOs) =>
        Replace(extractedBinaryPath, destinationPath, windows, macOs);

    /// <summary>
    /// Starts a child that inherits this console, or discards its output when <paramref name="discardOutput"/> is set,
    /// and waits at most <paramref name="timeout"/>. A child still running at the bound has its whole process tree
    /// stopped, and the caller gets a <see cref="TimeoutException"/>: a hung step must not hang the update.
    /// </summary>
    private static int StartFinishingProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        bool discardOutput,
        TimeSpan timeout)
    {
        ProcessStartInfo child = new(fileName) { UseShellExecute = false };
        if (discardOutput)
        {
            child.RedirectStandardOutput = true;
            child.RedirectStandardError = true;
        }

        foreach (string argument in arguments)
            child.ArgumentList.Add(argument);

        using Process? started = Process.Start(child);
        if (started is null)
            throw new InvalidOperationException($"the process could not be started: {fileName}");

        // Redirected output must be drained, or a child that writes a lot blocks on a full pipe. With no handler attached,
        // the asynchronous reads discard what they read.
        if (discardOutput)
        {
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
        }

        if (started.WaitForExit(timeout))
            return started.ExitCode;

        try
        {
            if (!started.HasExited)
                started.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException)
        {
            // It exited between the check and the kill, or the tree kill itself failed (Process.Kill(true)
            // aggregates the per-process results into one exception when any member refuses to die). The
            // timeout below still reports the step.
        }

        started.WaitForExit(TimeSpan.FromSeconds(5));
        throw new TimeoutException(
            $"The process did not exit within {(int)timeout.TotalSeconds} seconds, and its process tree was stopped.");
    }

    /// <summary>
    /// Runs a verb and captures what it printed. A process that cannot start surfaces as the caller's named failure.
    /// A verb that outlasts <see cref="CaptureVerbTimeout"/> is killed and throws <see cref="TimeoutException"/>,
    /// which each caller names.
    /// </summary>
    private static ProcessResult RunCaptureCore(string fileName, IReadOnlyList<string> arguments)
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

        return ProcessRunner.Run(start, string.Empty, CaptureVerbTimeout);
    }

    private static int RunProcessCore(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new(fileName) { UseShellExecute = false };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process? process = Process.Start(start);
        if (process is null)
            throw new SelfUpdateException($"the KyberDash tray step could not start {fileName}.");

        // The tray step writes to this console, so its streams are inherited rather than captured, and the
        // runner cannot bound it. The wait is bounded instead: a tray that never returns must not hang the update.
        if (process.WaitForExit(TrayUpdateTimeout))
            return process.ExitCode;

        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // It exited between the check and the kill. The timeout below still reports the step.
        }

        process.WaitForExit(TimeSpan.FromSeconds(5));
        throw new TimeoutException(
            $"The process did not exit within {(int)TrayUpdateTimeout.TotalSeconds} seconds.");
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

    /// <summary>
    ///     The bound for clearing quarantine on one staged file. xattr returns at once, so a longer wait means it hung.
    /// </summary>
    private static readonly TimeSpan XattrTimeout = TimeSpan.FromSeconds(30);

    private static void ExtractArchiveCore(string archivePath, string destinationDirectory, bool windows)
    {
        Directory.CreateDirectory(destinationDirectory);
        if (windows)
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            return;
        }

        using FileStream file = File.OpenRead(archivePath);
        using GZipStream gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destinationDirectory, overwriteFiles: true);
    }

    private static void Replace(
        string extractedBinaryPath,
        string destinationPath,
        bool windows,
        bool macOs)
    {
        if (!File.Exists(extractedBinaryPath))
        {
            throw new SelfUpdateException(
                $"archive did not contain {Path.GetFileName(extractedBinaryPath)}");
        }

        string directory = Path.GetDirectoryName(destinationPath)
            ?? throw new SelfUpdateException($"cannot install to '{destinationPath}'.");
        Directory.CreateDirectory(directory);

        string staging = Path.Combine(directory, "." + Path.GetFileName(destinationPath) + ".new");
        File.Copy(extractedBinaryPath, staging, overwrite: true);
        if (!windows)
            SetExecutable(staging);

        // Quarantine is cleared on the staging copy, before the move, not on the installed
        // path afterwards. The move carries extended attributes with the inode, so the
        // result is the same — but staging is the last point at which the destination is
        // certainly not this process's own image. See ClearMacQuarantine.
        if (macOs)
            ClearMacQuarantine(staging);

        if (windows && File.Exists(destinationPath))
        {
            string oldPath = destinationPath + ".old";
            File.Delete(oldPath);
            File.Move(destinationPath, oldPath, overwrite: true);
            try
            {
                File.Move(staging, destinationPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(oldPath) && !File.Exists(destinationPath))
                    File.Move(oldPath, destinationPath, overwrite: true);
                throw;
            }

            try
            {
                File.Delete(oldPath);
            }
            catch (IOException)
            {
                // The previous image stays locked until this process exits.
            }

            return;
        }

        File.Move(staging, destinationPath, overwrite: true);
    }

    /// <summary>Strips <c>com.apple.quarantine</c> from a staged binary.</summary>
    /// <remarks>
    /// Call this before the staged file is moved over its destination, never after. This is
    /// not the first code in an update to touch <c>System.Diagnostics.Process</c>, because the
    /// hand-off rehearsal starts a process before the first commit. A single-file host resolves
    /// a bundled assembly by re-opening its own executable by path. Once the destination is the running <c>kyber-weave</c>, that path holds the
    /// newly installed image and the load fails with "Could not load file or assembly
    /// 'System.Diagnostics.Process'" — aborting an update that had already written the new
    /// binary. Running before the move also warms the assembly for anything after it.
    /// </remarks>
    private static void ClearMacQuarantine(string path)
    {
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo("xattr")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add("com.apple.quarantine");
            startInfo.ArgumentList.Add(path);

            ProcessRunner.Run(startInfo, string.Empty, XattrTimeout);
        }
        catch (Exception)
        {
            // Best-effort: xattr is not required for the binaries to run. A hung xattr is stopped by the
            // runner at XattrTimeout and lands here like any other failure to clear the attribute.
        }
    }

    private static void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
