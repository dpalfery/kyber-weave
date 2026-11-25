using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Probes the files of a release's extraction tree and of the checkout the binary runs from.</summary>
public sealed class PhysicalSquadFileAccess : ISquadFileAccess
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    /// <inheritdoc />
    /// <remarks>
    /// Reads through a stream in chunks and stops at the cap, so a file that grew after the caller's length probe is
    /// never loaded whole. A missing parent directory is reported as <see cref="FileNotFoundException"/>, as for a
    /// missing file, so a caller handles one exception for both.
    /// </remarks>
    public byte[] ReadBytesUpTo(string path, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        try
        {
            using FileStream stream = File.OpenRead(path);
            using MemoryStream result = new();
            byte[] chunk = new byte[Math.Min(maxBytes, 81920)];
            int remaining = maxBytes;
            while (remaining > 0)
            {
                int read = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
                if (read == 0)
                {
                    break;
                }

                result.Write(chunk, 0, read);
                remaining -= read;
            }

            return result.ToArray();
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException(exception.Message, path, exception);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="FileInfo.Length"/> does not follow a symbolic link on Linux or macOS: it reports the link's own size,
    /// the length of the path it holds. So the path is resolved to the file the link finally names first, the way
    /// <see cref="WriteAllBytesAtomic"/> resolves it; a dangling link then names a missing file, whose length throws
    /// <see cref="FileNotFoundException"/> as the seam documents. A missing parent directory is the same case: the
    /// file is not there, so it is the same exception, never <see cref="DirectoryNotFoundException"/>.
    /// </remarks>
    public long GetLength(string path)
    {
        try
        {
            return new FileInfo(AtomicFile.ResolveLinks(Path.GetFullPath(path))).Length;
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new FileNotFoundException(exception.Message, path, exception);
        }
    }

    /// <inheritdoc />
    public void CreateDirectory(string path) => _ = Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteDirectory(string path) => Directory.Delete(path, true);

    /// <inheritdoc />
    public string NewScratchDirectoryPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public IEnumerable<SquadDirectoryEntry> EnumerateRealDirectories(string directory)
    {
        DirectoryInfo baseDir = new(directory);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        foreach (FileSystemInfo entry in baseDir.EnumerateFileSystemInfos("*", options))
        {
            if (entry is DirectoryInfo directoryInfo && entry.LinkTarget is null)
                yield return new SquadDirectoryEntry(directoryInfo.Name, directoryInfo.FullName);
        }
    }

    /// <inheritdoc />
    public string? FindCheckoutSquadSourceRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "products", "kyber-squad");
            if (File.Exists(Path.Combine(candidate, "squad.yml"))) return candidate;

            dir = dir.Parent;
        }

        return null;
    }

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] contents) =>
        AtomicFile.WriteAllBytes(path, contents, writeThroughSymlink: true, flushToDisk: false, preserveUnixFileMode: true);
}

/// <summary>Resolves canonical profiles from the checkout the binary runs from, then from the home snapshots.</summary>
/// <param name="files">Finds the checkout.</param>
/// <param name="loader">Loads the checkout's Squad source.</param>
/// <param name="store">Reads the home snapshots.</param>
public sealed class CheckoutOrSnapshotCanonicalProfileResolver(
    ISquadFileAccess files,
    ISquadSourceLoader loader,
    ISquadCanonicalProfileStore store) : ISquadCanonicalProfileResolver
{
    /// <inheritdoc />
    public SquadCanonicalProfiles? Resolve(string? preferredVersion)
    {
        SquadSource? source = TryLoadCheckoutSource();
        if (source is not null)
        {
            Dictionary<string, string> checkoutRoster = source.Agents.ToDictionary(
                agent => agent.Name,
                agent => agent.ModelProfile,
                StringComparer.OrdinalIgnoreCase);
            return new SquadCanonicalProfiles(preferredVersion ?? string.Empty, source.ModelProfiles, checkoutRoster);
        }

        if (!string.IsNullOrWhiteSpace(preferredVersion))
        {
            SquadCanonicalProfiles? versioned = TryReadVersionedSnapshot(preferredVersion);
            if (versioned is not null)
                return versioned;
        }

        return store.TryReadLatest();
    }

    private SquadSource? TryLoadCheckoutSource()
    {
        try
        {
            string? root = files.FindCheckoutSquadSourceRoot();
            return root is null ? null : loader.Load(root);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is Validation.SquadSourceValidationException)
        {
            return null;
        }
    }

    private SquadCanonicalProfiles? TryReadVersionedSnapshot(string version)
    {
        try
        {
            return store.TryRead(version);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
