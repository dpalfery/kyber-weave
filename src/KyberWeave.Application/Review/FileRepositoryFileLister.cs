using System.ComponentModel;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Processes;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Data.Review;

/// <summary>Lists a repository's files from git when it can answer, otherwise by walking the file system.</summary>
public sealed class FileRepositoryFileLister : IRepositoryFileLister
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    private readonly IProcessRunner _programs;
    private readonly IFileSystem _files;

    /// <summary>Creates a repository file lister over an async-capable process runner and file-system port.</summary>
    /// <param name="programs">Starts git without blocking a thread.</param>
    /// <param name="files">Answers every existence, listing and link question.</param>
    public FileRepositoryFileLister(IProcessRunner programs, IFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(programs);
        ArgumentNullException.ThrowIfNull(files);
        _programs = programs;
        _files = files;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        if (!_files.DirectoryExists(repositoryRoot))
            throw new DirectoryNotFoundException($"Repository root '{repositoryRoot}' does not exist.");

        IReadOnlyList<string> listed = await ListFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        return listed
            .Where(file => IsRegularFile(repositoryRoot, file))
            .ToArray();
    }

    /// <summary>Repository-relative files, from git without blocking when possible, otherwise from a walk.</summary>
    private async Task<IReadOnlyList<string>> ListFilesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? fromGit = await ListGitFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (fromGit is not null)
            return FilterGitFiles(fromGit);

        return WalkFiles(repositoryRoot);
    }

    private static string[] FilterGitFiles(IReadOnlyList<string> fromGit) =>
        fromGit.Where(file => !ProjectRootDetection.IsUnderExcludedDirectory(file)).ToArray();

    /// <summary>
    /// Lists tracked and untracked-but-not-ignored files without blocking a thread. Returns <see langword="null"/>
    /// when git is missing, no async runner was supplied, or the directory is not a work tree.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ListGitFilesAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _programs.RunAsync(
                "git",
                ["ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                repositoryRoot,
                GitTimeout,
                maxOutputBytes: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            return null;
        }

        return ParseGitFiles(result);
    }

    private static string[]? ParseGitFiles(ProcessResult result) =>
        result.ExitCode != 0
            ? null
            : result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Walks one directory level at a time. Links are skipped rather than followed, so the walk cannot
    /// leave the repository, and excluded directories are never entered.
    /// </summary>
    private IReadOnlyList<string> WalkFiles(string repositoryRoot)
    {
        List<string> found = [];
        Stack<string> pending = new();
        pending.Push(string.Empty);

        while (pending.Count > 0)
        {
            string relative = pending.Pop();
            string directory = relative.Length == 0 ? repositoryRoot : Path.Combine(repositoryRoot, relative);

            foreach (FileSystemEntry entry in ListEntries(directory))
            {
                if (entry.IsLink)
                    continue;

                string name = Path.GetFileName(entry.Path);
                string child = relative.Length == 0 ? name : $"{relative}/{name}";
                if (entry.IsDirectory)
                {
                    if (!ProjectRootDetection.ExcludedDirectories.Contains(name))
                        pending.Push(child);
                }
                else
                {
                    found.Add(child);
                }
            }
        }

        return found;
    }

    /// <summary>A directory's immediate entries, or none when it cannot be listed, so one locked folder does not stop the walk.</summary>
    private IReadOnlyList<FileSystemEntry> ListEntries(string directory)
    {
        try
        {
            return _files.EnumerateEntries(directory).ToArray();
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return [];
        }
    }

    /// <summary>A regular file that still exists. A deleted-but-tracked file and a link are not markers.</summary>
    private bool IsRegularFile(string repositoryRoot, string file)
    {
        string path = Path.Combine(repositoryRoot, file);
        return _files.FileExists(path) && _files.GetLinkTarget(path) is null;
    }
}
