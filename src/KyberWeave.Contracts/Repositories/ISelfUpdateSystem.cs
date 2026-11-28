using KyberWeave.Core.Processes;

namespace KyberWeave.Contracts.Repositories;

/// <summary>The processes, files and archives a self-update touches.</summary>
/// <remarks>Each process method bounds its wait and stops the child's whole process tree on expiry.</remarks>
public interface ISelfUpdateSystem
{
    /// <summary>Runs a KyberDash verb that inherits this console, waits at most five minutes and returns its exit code.</summary>
    /// <param name="fileName">The program to start.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="TimeoutException">The verb outlasted its bound and its process tree was stopped.</exception>
    int RunProcess(string fileName, IReadOnlyList<string> arguments);

    /// <summary>Runs a verb and captures what it printed without blocking, waiting at most two minutes.</summary>
    /// <param name="fileName">The program to start.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <param name="cancellationToken">Cancels the run and stops its process tree.</param>
    /// <returns>The exit code and captured output.</returns>
    /// <exception cref="TimeoutException">The verb outlasted its bound and its process tree was stopped.</exception>
    Task<ProcessResult> RunCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>Starts the installed kyber-weave to finish an update, or to rehearse that start with its output discarded.</summary>
    /// <param name="fileName">The program to start.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <param name="discardOutput">Whether the child's output is discarded; the rehearsal does, with a 30 second bound.</param>
    /// <returns>The exit code.</returns>
    /// <exception cref="TimeoutException">The child outlasted its bound and its process tree was stopped.</exception>
    int StartFinishingProcess(string fileName, IReadOnlyList<string> arguments, bool discardOutput);

    /// <summary>Whether a file can be created in the directory.</summary>
    /// <param name="directory">The install directory.</param>
    /// <returns>True when a probe file could be created and removed.</returns>
    bool CanWriteDirectory(string directory);

    /// <summary>Extracts a Release archive over a destination directory.</summary>
    /// <param name="archivePath">The downloaded archive.</param>
    /// <param name="destinationDirectory">Where to extract.</param>
    /// <param name="windows">Whether the archive is a Windows <c>.zip</c> rather than a <c>.tar.gz</c>.</param>
    void ExtractArchive(string archivePath, string destinationDirectory, bool windows);

    /// <summary>Copies a file, so a binary can be backed up before replacement and restored when a later commit fails.</summary>
    /// <param name="sourcePath">The file to copy.</param>
    /// <param name="destinationPath">Where the copy goes.</param>
    /// <param name="overwrite">True to replace an existing destination; false to fail when one is there.</param>
    /// <remarks>
    /// The physical adapter carries the source's permissions, so a restored binary is still executable. An in-memory
    /// fake models content only; the executable bit is proven against the real adapter.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The source does not exist.</exception>
    /// <exception cref="IOException">The copy could not be made.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not read the source or write the destination.</exception>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>Replaces an installed binary with an extracted one without blocking a thread, keeping the old image until the new one is in place on Windows.</summary>
    /// <param name="extractedBinaryPath">The new binary.</param>
    /// <param name="destinationPath">The installed binary to replace.</param>
    /// <param name="windows">Whether the platform is Windows.</param>
    /// <param name="macOs">Whether the platform is macOS, which clears the quarantine attribute first.</param>
    /// <param name="cancellationToken">Cancels the quarantine-attribute child.</param>
    Task ReplaceBinaryAsync(
        string extractedBinaryPath,
        string destinationPath,
        bool windows,
        bool macOs,
        CancellationToken cancellationToken = default);
}
