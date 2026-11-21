using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KyberWeave.Core.FileSystem;

/// <summary>
/// Opens a file whose every write lands at its end, with one <c>write(2)</c> per buffer, so parallel processes append
/// whole lines instead of writing over each other's.
/// </summary>
/// <remarks>
/// The framework's own append mode seeks to the end once, when the file is opened, and then writes at that remembered
/// offset, so two processes that open the file together hold the same position and the slower one overwrites the line
/// that landed meanwhile. On Unix an <c>O_APPEND</c> descriptor makes the kernel place every write at the end instead,
/// which is what the shared budget, session and claim files need. Windows keeps the framework's append mode.
/// </remarks>
internal static class AppendFile
{
    /// <summary>O_WRONLY | O_APPEND | O_CLOEXEC, as the platform spells it.</summary>
    private const int AppendLinux = 0x1 | 0x400 | 0x80000;

    /// <summary>The same flags with macOS's own values.</summary>
    private const int AppendMac = 0x1 | 0x8 | 0x1000000;

    /// <summary>LOCK_SH | LOCK_NB: take the shared lock or report that somebody holds it alone.</summary>
    private const int LockSharedNonBlocking = 0x1 | 0x4;

    /// <summary>Opens a file for appending, creating it when it is missing.</summary>
    /// <param name="path">The file.</param>
    /// <returns>A write-only stream that appends and owns the descriptor; the caller disposes it.</returns>
    /// <exception cref="DirectoryNotFoundException">A directory on the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">The process may not open the file, or a directory is in its place.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    public static Stream Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }

        // O_CREAT is a variadic flag on POSIX, and managed code cannot pass its mode argument safely there, so the
        // file is created first and then opened with the two-argument form.
        if (!File.Exists(path))
        {
            using FileStream created = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        }

        int descriptor = OpenAppend(path, OperatingSystem.IsMacOS() ? AppendMac : AppendLinux);
        if (descriptor < 0)
        {
            throw new IOException(
                $"The file '{path}' could not be opened for appending.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        // Another appender or reader shares the lock; the framework's exclusive opener holds it alone, and the
        // contract says an append must be refused while that one is held.
        if (LockFile(descriptor, LockSharedNonBlocking) < 0)
        {
            int error = Marshal.GetLastWin32Error();
            _ = CloseFile(descriptor);
            throw new IOException(
                $"The file '{path}' is held by an exclusive opener.",
                new Win32Exception(error));
        }

        // The stream takes the handle over and disposes it, so the analyzer's "dispose before scope ends" does not
        // apply; the stream's Dispose is the one that ends it.
#pragma warning disable CA2000
        return new AppendStream(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true));
#pragma warning restore CA2000
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAppend([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LockFile(int descriptor, int operation);

    [DllImport("libc", EntryPoint = "close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CloseFile(int descriptor);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint WriteBytes(SafeFileHandle descriptor, byte[] buffer, nint count);

    /// <summary>A write-only stream over an <c>O_APPEND</c> descriptor: each write is one append, never buffered.</summary>
    private sealed class AppendStream(SafeFileHandle handle) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // Every write is already in the file.
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - offset);

            byte[] pending = offset == 0 && count == buffer.Length ? buffer : buffer[offset..(offset + count)];
            while (pending.Length > 0)
            {
                nint written = WriteBytes(handle, pending, pending.Length);
                if (written < 0)
                {
                    throw new IOException("The file could not be appended to.", new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if (written == 0)
                {
                    // A write that takes nothing would spin here forever; report it as the fault it is.
                    throw new IOException("The file accepted none of the bytes offered to it.");
                }

                pending = pending[(int)written..];
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                handle.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
