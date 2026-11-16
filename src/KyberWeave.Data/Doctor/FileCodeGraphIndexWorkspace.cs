using System.Diagnostics;
using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Data.Doctor;

/// <summary>Holds the CodeGraph index lock as an exclusively opened file and probes writability on the real disk.</summary>
public sealed class FileCodeGraphIndexWorkspace : ICodeGraphIndexWorkspace
{
    /// <summary>How long a waiting run sleeps between attempts to take the lock.</summary>
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public IDisposable? TryAcquireLock(string path, TimeSpan wait, out string? failure)
    {
        failure = null;
        DateTime deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (DirectoryNotFoundException)
            {
                // A missing lock directory is not contention, and retrying it until the deadline would only delay
                // the same answer.
                failure = $"the index lock directory for '{path}' does not exist";
                return null;
            }
            catch (PathTooLongException)
            {
                failure = $"the index lock path '{path}' is too long";
                return null;
            }
            catch (IOException ex) when (CodeGraphIndexRules.IsSharingViolation(ex) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(LockPollInterval);
            }
            catch (IOException ex) when (CodeGraphIndexRules.IsSharingViolation(ex))
            {
                return null;
            }
            catch (IOException ex)
            {
                failure = $"the index lock '{path}' could not be opened: {ex.Message}";
                return null;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                failure = $"the index lock '{path}' could not be opened";
                return null;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>The probe file is the only file this code ever removes.</remarks>
    public bool IsWritable(string directory)
    {
        string probe = Path.Combine(directory, $".doctor-write-{Guid.NewGuid():N}.probe");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public bool IsCodeGraphLockHeld(string indexDirectory)
    {
        string lockFile = Path.Combine(indexDirectory, CodeGraphIndexRules.IndexLockFileName);
        try
        {
            if (!File.Exists(lockFile)
                || !int.TryParse(File.ReadAllText(lockFile).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
                return false;

            using Process holder = Process.GetProcessById(processId);
            return !holder.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // No such process, or a lock file that vanished or cannot be read: CodeGraph treats both as not held.
            return false;
        }
    }
}
