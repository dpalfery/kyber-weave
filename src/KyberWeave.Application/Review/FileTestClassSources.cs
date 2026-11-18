using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Lists the <c>.cs</c> files under a repository root and reads the namespace they declare.</summary>
/// <param name="files">Answers every existence, listing, link and read question.</param>
/// <param name="environment">Names the working directory and the program's own directory.</param>
/// <param name="paths">Resolves a root the caller spelled relatively.</param>
public sealed class FileTestClassSources(
    IFileSystem files,
    IProcessEnvironment environment,
    IPathResolver paths) : ITestClassSources
{
    private const string SourceExtension = ".cs";

    /// <inheritdoc />
    public TestSourceTree? Open(string? root)
    {
        string candidateRoot = root ?? DefaultRepositoryRoot();
        if (string.IsNullOrWhiteSpace(candidateRoot))
            return null;

        // Resolve first: a root the caller spelled relatively names the working directory's repository, and probing
        // the spelling would ask the file system about a path that only exists relative to the process.
        string repository;
        try
        {
            repository = paths.GetFullPath(candidateRoot, environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!files.DirectoryExists(repository))
            return null;

        try
        {
            _ = files.EnumerateEntries(repository).FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string target = root ?? candidateRoot;
            throw new InvalidOperationException($"The repository root '{target}' is not accessible: {ex.Message}", ex);
        }

        return new TestSourceTree(repository, SourceFilesByName(repository));
    }

    /// <inheritdoc />
    public string? ReadDeclaredNamespace(string filePath)
    {
        try
        {
            return TestClassPlanning.DeclaredNamespaceOf(files.ReadLines(filePath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The repository root of the running process, found by walking up from the working directory and then
    /// from the program's own directory.
    /// </summary>
    /// <remarks>
    /// A caller that plans gates without naming a root — a unit test, or a command run from inside the tree —
    /// still resolves against the tree it runs in. A process started outside any repository resolves nothing,
    /// which skips the gate rather than running an empty filter.
    /// </remarks>
    private string DefaultRepositoryRoot()
    {
        string workingDirectory = environment.CurrentDirectory;
        foreach (string start in new[] { workingDirectory, environment.ApplicationBaseDirectory })
        {
            if (NearestRepositoryRoot(start) is { } found)
                return found;
        }

        return workingDirectory;
    }

    private string? NearestRepositoryRoot(string start)
    {
        string? current = Path.TrimEndingDirectorySeparator(paths.GetFullPath(start, environment.CurrentDirectory));
        while (current is not null)
        {
            // A worktree carries .git as a file and a checkout carries it as a directory.
            if (files.FileExists(Path.Combine(current, ".git")) ||
                files.DirectoryExists(Path.Combine(current, ".git")) ||
                files.DirectoryExists(Path.Combine(current, ".kyber-weave")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>Every <c>.cs</c> file under the root, keyed by its file name without the extension.</summary>
    /// <remarks>
    /// One walk serves every lookup, and the first file found for a name wins, so a class declared in a file
    /// of the same name resolves the same way on every run.
    /// </remarks>
    private Dictionary<string, string> SourceFilesByName(string root)
    {
        Dictionary<string, string> byName = new(StringComparer.Ordinal);
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (FileSystemEntry entry in ListEntries(directory))
            {
                // A link is never followed: it can lead outside the repository, and the file
                // behind it is not this repository's source.
                if (entry.IsLink)
                    continue;

                string name = Path.GetFileName(entry.Path);
                if (entry.IsDirectory)
                {
                    if (!TestClassPlanning.GeneratedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                        pending.Push(entry.Path);

                    continue;
                }

                if (!name.EndsWith(SourceExtension, StringComparison.Ordinal))
                    continue;

                byName.TryAdd(name[..^SourceExtension.Length], entry.Path);
            }
        }

        return byName;
    }

    /// <summary>A directory's immediate entries, or none when it cannot be listed, so one locked folder does not stop the walk.</summary>
    private IReadOnlyList<FileSystemEntry> ListEntries(string directory)
    {
        try
        {
            return files.EnumerateEntries(directory).ToArray();
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return [];
        }
    }
}
