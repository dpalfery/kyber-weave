using System.Text;
using KyberWeave.Kernel.FileSystem;

namespace KyberWeave.Data.Doctor;

/// <summary>Reads the real file system for a doctor probe.</summary>
public sealed class PhysicalDoctorFileSystem : IDoctorFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool IsPresentFile(string path)
    {
        FileInfo info = new(path);
        return info.LinkTarget is null
            ? info.Exists
            : info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true };
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public IReadOnlyList<DoctorDirectoryEntry> ListEntries(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        EnumerationOptions options = new() { RecurseSubdirectories = false, AttributesToSkip = 0 };
        List<DoctorDirectoryEntry> entries = [];
        foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
        {
            entries.Add(new DoctorDirectoryEntry(
                entry.Name,
                entry.FullName,
                entry is DirectoryInfo,
                entry.LinkTarget is not null));
        }

        return entries;
    }

    public string ReadTail(string path, long maxBytes, out bool firstLineIsPartial)
    {
        // VS Code holds the log open for writing, so the file is opened with read-write sharing.
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool cut = stream.Length > maxBytes;
        firstLineIsPartial = false;
        if (cut)
        {
            stream.Seek(-maxBytes - 1, SeekOrigin.End);
            firstLineIsPartial = stream.ReadByte() != '\n';
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
