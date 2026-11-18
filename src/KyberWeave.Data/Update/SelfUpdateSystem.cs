using System.Diagnostics;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Update;

/// <summary>The processes, files and archives the self-update touches: starts children, probes the install directory and replaces binaries.</summary>
/// <remarks>
/// It holds the bounds and the choice of which seam runs each step, and calls no resource itself. Files go through
/// <see cref="IWritableFileSystem"/>, bounded captures through <see cref="IBoundedProcessExecutor"/> and entry paths
/// through <see cref="IPathResolver"/>. The two things those seams
/// cannot express are named adapters: a child that shares this console (<see cref="IConsoleProcessLauncher"/>, which
/// also runs the hand-off rehearsal so the hand-off's own start path is the one rehearsed) and permission bits
/// (<see cref="IUpdateFileModes"/>).
/// </remarks>
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

    private const string ProbeFileName = ".kyber-weave-update-probe";

    private readonly IWritableFileSystem _files;
    private readonly IBoundedProcessExecutor _processes;
    private readonly IConsoleProcessLauncher _console;
    private readonly ReleaseArchiveExtractor _extractor;
    private readonly BinaryReplacer _replacer;

    /// <summary>Creates the system over its seams.</summary>
    /// <param name="files">Every file the update reads, stages, moves or deletes.</param>
    /// <param name="paths">Decides where an archive entry may be written.</param>
    /// <param name="processes">Runs the bounded verbs whose output is captured.</param>
    /// <param name="console">Runs the steps that write to this console, and the hand-off with its rehearsal.</param>
    /// <param name="modes">Reads and sets the permission bits of an installed binary.</param>
    public SelfUpdateSystem(
        IWritableFileSystem files,
        IPathResolver paths,
        IBoundedProcessExecutor processes,
        IConsoleProcessLauncher console,
        IUpdateFileModes modes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(modes);
        _files = files;
        _processes = processes;
        _console = console;
        _extractor = new ReleaseArchiveExtractor(files, new ArchiveEntryPlanner(paths), modes);
        _replacer = new BinaryReplacer(files, modes, processes);
    }

    /// <inheritdoc />
    public int RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            return _console.Run(fileName, arguments, TrayUpdateTimeout);
        }
        catch (InvalidOperationException)
        {
            throw new SelfUpdateException($"the KyberDash tray step could not start {fileName}.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"The process did not exit within {(int)TrayUpdateTimeout.TotalSeconds} seconds.");
        }
    }

    /// <inheritdoc />
    public ProcessResult RunCapture(string fileName, IReadOnlyList<string> arguments)
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

        return _processes.Run(start, string.Empty, CaptureVerbTimeout);
    }

    /// <inheritdoc />
    public int StartFinishingProcess(string fileName, IReadOnlyList<string> arguments, bool discardOutput) =>
        _console.Run(
            fileName,
            arguments,
            discardOutput ? RehearsalTimeout : FinishUpdateTimeout,
            discardOutput);

    /// <inheritdoc />
    public bool CanWriteDirectory(string directory)
    {
        string probe = Path.Combine(directory, ProbeFileName);
        try
        {
            // A probe a killed update left is removed first, so it never reads as an unwritable directory.
            _files.DeleteFile(probe);
            using (_files.OpenWriteNew(probe))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                _files.DeleteFile(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A probe that cannot be removed is removed by the next probe.
            }
        }
    }

    /// <inheritdoc />
    public void ExtractArchive(string archivePath, string destinationDirectory, bool windows) =>
        _extractor.Extract(archivePath, destinationDirectory, windows);

    /// <inheritdoc />
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite) =>
        _replacer.Copy(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void ReplaceBinary(string extractedBinaryPath, string destinationPath, bool windows, bool macOs) =>
        _replacer.Replace(extractedBinaryPath, destinationPath, windows, macOs);
}
