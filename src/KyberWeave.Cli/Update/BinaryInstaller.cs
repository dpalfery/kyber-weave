using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using KyberWeave.Core.Processes;

namespace KyberWeave.Cli.Update;

/// <summary>Extracts a Release archive and atomically replaces binaries in the install directory.</summary>
internal static class BinaryInstaller
{
    internal static string ArchiveName(string binaryBaseName, string rid)
    {
        string extension = PlatformRid.IsWindowsRid(rid) ? ".zip" : ".tar.gz";
        return $"{binaryBaseName}-{rid}{extension}";
    }

    internal static string InstalledFileName(string binaryBaseName, bool windows) =>
        windows ? binaryBaseName + ".exe" : binaryBaseName;

    internal static void ExtractArchive(string archivePath, string destinationDirectory, bool windows)
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

    internal static void Replace(
        string extractedBinaryPath,
        string destinationPath,
        bool windows)
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

    internal static void ClearMacQuarantine(string path)
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

            ProcessRunner.Run(startInfo, string.Empty);
        }
        catch (Exception)
        {
            // Best-effort: xattr is not required for the binaries to run.
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
