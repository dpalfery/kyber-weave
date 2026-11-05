using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Windows implementation of <see cref="ISquadLinkFileSystem"/> using NTFS hard links for files
/// and NTFS junctions (mount points) for directories, requiring no administrator privileges or Developer Mode.
/// </summary>
public sealed class WindowsSquadLinkFileSystem : ISquadLinkFileSystem
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FileShareReadWriteDelete = 0x00000007; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    private const uint CreationDispositionOpenExisting = 3; // OPEN_EXISTING
    private const uint FileFlagBackupSemantics = 0x02000000; // FILE_FLAG_BACKUP_SEMANTICS
    private const uint FileFlagOpenReparsePoint = 0x00200000; // FILE_FLAG_OPEN_REPARSE_POINT
    private const uint FileAccessGenericWrite = 0x40000000;
    private const int ErrorNotSameDevice = 17; // ERROR_NOT_SAME_DEVICE

    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WindowsSquadLinkFileSystem is supported only on Windows.");
        }

        if (!File.Exists(targetPath))
        {
            throw new SquadLinkException($"Target file '{targetPath}' does not exist.", linkPath, targetPath);
        }

        EnsureDirectoryExists(Path.GetDirectoryName(linkPath));

        bool success = CreateHardLink(linkPath, targetPath, IntPtr.Zero);
        if (!success)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotSameDevice)
            {
                throw new SquadLinkException(
                    $"Cannot create hard link across different filesystem volumes: '{linkPath}' -> '{targetPath}'.",
                    linkPath,
                    targetPath,
                    new Win32Exception(error));
            }

            throw new SquadLinkException(
                $"Failed to create hard link '{linkPath}' to '{targetPath}': {new Win32Exception(error).Message}",
                linkPath,
                targetPath,
                new Win32Exception(error));
        }
    }

    /// <inheritdoc />
    public void CreateFolderLink(string linkPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WindowsSquadLinkFileSystem is supported only on Windows.");
        }

        if (!Directory.Exists(targetPath))
        {
            throw new SquadLinkException($"Target directory '{targetPath}' does not exist.", linkPath, targetPath);
        }

        string fullTargetPath = Path.GetFullPath(targetPath).TrimEnd('\\', '/');
        string substituteName = @"\??\" + fullTargetPath;
        string printName = fullTargetPath;

        byte[] subBytes = Encoding.Unicode.GetBytes(substituteName);
        byte[] printBytes = Encoding.Unicode.GetBytes(printName);

        // Path buffer contains substitute name and print name with null terminators.
        int pathBufferLength = subBytes.Length + 2 + printBytes.Length + 2;
        int dataLength = 8 + pathBufferLength;
        int totalSize = 8 + dataLength;

        byte[] buffer = new byte[totalSize];
        using (MemoryStream stream = new(buffer))
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(IoReparseTagMountPoint);
            writer.Write((ushort)dataLength);
            writer.Write((ushort)0); // Reserved

            writer.Write((ushort)0); // SubstituteNameOffset
            writer.Write((ushort)subBytes.Length);
            writer.Write((ushort)(subBytes.Length + 2)); // PrintNameOffset
            writer.Write((ushort)printBytes.Length);

            writer.Write(subBytes);
            writer.Write((ushort)0);
            writer.Write(printBytes);
            writer.Write((ushort)0);
        }

        EnsureDirectoryExists(Path.GetDirectoryName(linkPath));
        if (!Directory.Exists(linkPath))
        {
            Directory.CreateDirectory(linkPath);
        }

        using SafeFileHandle handle = CreateFile(
            linkPath,
            FileAccessGenericWrite,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            CreationDispositionOpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new SquadLinkException(
                $"Failed to open directory '{linkPath}' to set junction: {new Win32Exception(error).Message}",
                linkPath,
                targetPath,
                new Win32Exception(error));
        }

        bool success = DeviceIoControl(
            handle,
            FsctlSetReparsePoint,
            buffer,
            (uint)buffer.Length,
            IntPtr.Zero,
            0,
            out uint _,
            IntPtr.Zero);

        if (!success)
        {
            int error = Marshal.GetLastWin32Error();
            throw new SquadLinkException(
                $"Failed to set NTFS junction reparse point on '{linkPath}': {new Win32Exception(error).Message}",
                linkPath,
                targetPath,
                new Win32Exception(error));
        }
    }

    /// <inheritdoc />
    public bool IsLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (Directory.Exists(path))
        {
            DirectoryInfo dirInfo = new(path);
            if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || dirInfo.LinkTarget is not null)
            {
                return true;
            }

            return false;
        }

        if (File.Exists(path))
        {
            FileInfo fileInfo = new(path);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || fileInfo.LinkTarget is not null)
            {
                return true;
            }

            if (TryGetFileInformation(path, out ByHandleFileInformation info))
            {
                return info.NumberOfLinks > 1;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsLinkTo(string linkPath, string expectedTarget)
    {
        if (string.IsNullOrWhiteSpace(linkPath) || string.IsNullOrWhiteSpace(expectedTarget))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (Directory.Exists(linkPath))
        {
            DirectoryInfo dirInfo = new(linkPath);
            if (!dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) && dirInfo.LinkTarget is null)
            {
                return false;
            }

            string? target = dirInfo.LinkTarget;
            if (target is null)
            {
                return false;
            }

            if (target.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                target = target[4..];
            }

            if (string.Equals(target, expectedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string normalizedTarget = Path.GetFullPath(target).TrimEnd('\\', '/');
            string normalizedExpected = Path.GetFullPath(expectedTarget).TrimEnd('\\', '/');
            return string.Equals(normalizedTarget, normalizedExpected, StringComparison.OrdinalIgnoreCase);
        }

        if (File.Exists(linkPath) && File.Exists(expectedTarget))
        {
            if (TryGetFileInformation(linkPath, out ByHandleFileInformation linkInfo) &&
                TryGetFileInformation(expectedTarget, out ByHandleFileInformation targetInfo))
            {
                return linkInfo.VolumeSerialNumber == targetInfo.VolumeSerialNumber
                       && linkInfo.FileIndexHigh == targetInfo.FileIndexHigh
                       && linkInfo.FileIndexLow == targetInfo.FileIndexLow;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        if (!Directory.Exists(directoryPath) && !File.Exists(directoryPath) && !IsLink(directoryPath))
        {
            return;
        }

        if (IsLink(directoryPath))
        {
            DirectoryInfo rootInfo = new(directoryPath);
            if (rootInfo.Attributes.HasFlag(FileAttributes.Directory) ||
                rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(directoryPath, false);
            }
            else
            {
                File.Delete(directoryPath);
            }

            return;
        }

        DirectoryInfo dirInfo = new(directoryPath);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };

        List<FileSystemInfo> entries = dirInfo.EnumerateFileSystemInfos("*", options).ToList();

        foreach (FileSystemInfo entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || entry.LinkTarget is not null)
            {
                if (entry is DirectoryInfo || entry.Attributes.HasFlag(FileAttributes.Directory))
                {
                    Directory.Delete(entry.FullName, false);
                }
                else
                {
                    File.Delete(entry.FullName);
                }
            }
            else if (entry is DirectoryInfo subDirectory)
            {
                DeleteTreeNoFollow(subDirectory.FullName);
            }
            else
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(entry.FullName, FileAttributes.Normal);
                }

                File.Delete(entry.FullName);
            }
        }

        Directory.Delete(directoryPath, false);
    }

    private static bool TryGetFileInformation(string path, out ByHandleFileInformation fileInfo)
    {
        fileInfo = default;
        using SafeFileHandle handle = CreateFile(
            path,
            0,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            CreationDispositionOpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return false;
        }

        return GetFileInformationByHandle(handle, out fileInfo);
    }

    private static void EnsureDirectoryExists(string? directoryPath)
    {
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);
}
