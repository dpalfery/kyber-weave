using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Fakes;

/// <summary>The self-update system in a unit test: it records every step and performs its file work on the file-system fake.</summary>
/// <remarks>
/// The archives are scripted, so nothing is extracted from a real one, and a replacement is a byte copy inside the
/// in-memory file system. A test that wants a step to fail sets the matching result or failure and asserts what the
/// updater did around it. The in-memory file system models content, not permissions: the executable bit a real copy
/// carries is proven against the disk adapter in the integration project, not here.
/// </remarks>
public sealed class FakeSelfUpdateSystem(IWritableFileSystem files) : ISelfUpdateSystem
{
    private readonly List<string> _replacedDestinations = [];
    private readonly List<string> _extractedArchives = [];
    private readonly List<(string Source, string Destination)> _copies = [];
    private readonly List<string> _startedPrograms = [];

    /// <summary>Gets the destinations a replacement committed, in order.</summary>
    public IReadOnlyList<string> ReplacedDestinations => _replacedDestinations;

    /// <summary>Gets the archive paths whose extraction was asked for, in order.</summary>
    public IReadOnlyList<string> ExtractedArchives => _extractedArchives;

    /// <summary>Gets the backup and restore copies, in order.</summary>
    public IReadOnlyList<(string Source, string Destination)> Copies => _copies;

    /// <summary>Gets the programs a run, capture or hand-off started, in order.</summary>
    public IReadOnlyList<string> StartedPrograms => _startedPrograms;

    /// <summary>Gets or sets the exit code <see cref="RunProcess"/> answers.</summary>
    public int RunProcessExitCode { get; set; }

    /// <summary>Gets or sets what <see cref="RunCapture"/> answers.</summary>
    public ProcessResult CaptureResult { get; set; } = new(0, string.Empty, string.Empty);

    /// <summary>Gets or sets the exit code <see cref="StartFinishingProcess"/> answers.</summary>
    public int StartFinishingProcessExitCode { get; set; }

    /// <summary>Gets or sets what <see cref="CanWriteDirectory"/> answers.</summary>
    public bool CanWriteDirectoryResult { get; set; } = true;

    /// <summary>Gets or sets the extraction the update asks for; it creates the files the updater will find.</summary>
    public Action<string, string> Extract { get; set; } = (_, _) => { };

    /// <summary>Gets or sets the failure a replacement of one destination raises; null commits it.</summary>
    public Func<string, Exception?>? ReplaceFailure { get; set; }

    /// <inheritdoc />
    public int RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        _startedPrograms.Add(fileName);
        return RunProcessExitCode;
    }

    /// <inheritdoc />
    public ProcessResult RunCapture(string fileName, IReadOnlyList<string> arguments)
    {
        _startedPrograms.Add(fileName);
        return CaptureResult;
    }

    /// <inheritdoc />
    public int StartFinishingProcess(string fileName, IReadOnlyList<string> arguments, bool discardOutput)
    {
        _startedPrograms.Add(fileName);
        return StartFinishingProcessExitCode;
    }

    /// <inheritdoc />
    public bool CanWriteDirectory(string directory) => CanWriteDirectoryResult;

    /// <inheritdoc />
    public void ExtractArchive(string archivePath, string destinationDirectory, bool windows)
    {
        _extractedArchives.Add(archivePath);
        Extract(archivePath, destinationDirectory);
    }

    /// <inheritdoc />
    public void ReplaceBinary(string extractedBinaryPath, string destinationPath, bool windows, bool macOs)
    {
        if (ReplaceFailure?.Invoke(destinationPath) is { } failure)
            throw failure;

        _replacedDestinations.Add(destinationPath);
        files.WriteAllBytes(destinationPath, files.ReadAllBytes(extractedBinaryPath));
    }

    /// <inheritdoc />
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!files.FileExists(sourcePath))
            throw new FileNotFoundException($"Could not find file '{sourcePath}'.", sourcePath);
        if (!overwrite && files.PathExists(destinationPath))
            throw new IOException($"The file '{destinationPath}' already exists.");

        _copies.Add((sourcePath, destinationPath));
        files.WriteAllBytes(destinationPath, files.ReadAllBytes(sourcePath));
    }
}
