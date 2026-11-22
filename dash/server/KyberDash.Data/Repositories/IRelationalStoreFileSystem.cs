using System;
using System.IO;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Minimal filesystem seam used by <see cref="SqliteRelationalStore"/> for existence checks,
/// parent directory creation, and future rebuild cleanup without exposing read, copy, or move operations.
/// </summary>
public interface IRelationalStoreFileSystem
{
    /// <summary>Returns <see langword="true"/> when a file exists at <paramref name="path"/>.</summary>
    /// <param name="path">Filesystem path to check.</param>
    bool FileExists(string path);

    /// <summary>Ensures the directory at <paramref name="path"/> exists.</summary>
    /// <param name="path">Directory path to create if absent.</param>
    void CreateDirectory(string path);

    /// <summary>Deletes the file at <paramref name="path"/> when present.</summary>
    /// <param name="path">File path to delete.</param>
    void DeleteFile(string path);
}

/// <summary>
/// Default <see cref="IRelationalStoreFileSystem"/> backed by the physical local filesystem.
/// </summary>
public class PhysicalRelationalStoreFileSystem : IRelationalStoreFileSystem
{
    /// <summary>Shared singleton instance for production store operations.</summary>
    public static PhysicalRelationalStoreFileSystem Instance { get; } = new();

    /// <inheritdoc />
    public bool FileExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
