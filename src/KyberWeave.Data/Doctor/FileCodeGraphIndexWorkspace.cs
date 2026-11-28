using System.Diagnostics;
using System.Globalization;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Doctor;

namespace KyberWeave.Data.Doctor;

/// <summary>Holds the CodeGraph index lock as an exclusively opened file and probes writability on the real disk.</summary>
public sealed class FileCodeGraphIndexWorkspace(TimeProvider time) : ICodeGraphIndexWorkspace
{
    /// <summary>How long a waiting run sleeps between attempts to take the lock.</summary>
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public async Task<CodeGraphIndexLockAttempt> TryAcquireLockAsync(
        string path,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
    {
        long started = time.GetTimestamp();
        while (true)
        {
            FileStream? held = null;
            try
            {
                held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                CodeGraphIndexLockAttempt attempt = new(held, Failure: null);
                held = null;
                return attempt;
            }
            catch (DirectoryNotFoundException)
            {
                // A missing lock directory is not contention, and retrying it until the deadline would only delay
                // the same answer.
                return new CodeGraphIndexLockAttempt(null, $"the index lock directory for '{path}' does not exist");
            }
            catch (PathTooLongException)
            {
                return new CodeGraphIndexLockAttempt(null, $"the index lock path '{path}' is too long");
            }
            catch (IOException ex) when (IsSharingViolation(ex) && time.GetElapsedTime(started) < wait)
            {
                await Task.Delay(LockPollInterval, time, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                return new CodeGraphIndexLockAttempt(null, Failure: null);
            }
            catch (IOException ex)
            {
                return new CodeGraphIndexLockAttempt(null, $"the index lock '{path}' could not be opened: {ex.Message}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return new CodeGraphIndexLockAttempt(null, $"the index lock '{path}' could not be opened");
            }
            finally
            {
                DisposeHeld(held);
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
        string lockFile = Path.Combine(indexDirectory, CodeGraphIndexFiles.IndexLockFileName);
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

    private static void DisposeHeld(FileStream? held) => held?.Dispose();

    /// <summary>
    ///     Whether an <see cref="IOException"/> represents a sharing or locking violation caused by another process.
    /// </summary>
    /// <param name="ex">The exception.</param>
    public static bool IsSharingViolation(IOException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        int hr = ex.HResult;
        if (hr is unchecked((int)0x80070020) or unchecked((int)0x80070021) || (hr & 0xFFFF) is 32 or 33)
            return true;

        string msg = ex.Message;
        return msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("sharing violation", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("locked", StringComparison.OrdinalIgnoreCase);
    }
}
