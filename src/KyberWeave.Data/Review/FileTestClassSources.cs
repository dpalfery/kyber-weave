using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Lists the <c>.cs</c> files under a repository root and reads the namespace they declare.</summary>
public sealed class FileTestClassSources : ITestClassSources
{
    private const string SourceExtension = ".cs";

    /// <inheritdoc />
    public TestSourceTree? Open(string? root)
    {
        string candidateRoot = root ?? DefaultRepositoryRoot();
        if (string.IsNullOrWhiteSpace(candidateRoot) || !Directory.Exists(candidateRoot))
            return null;

        string repository;
        try
        {
            _ = Directory.EnumerateFileSystemEntries(candidateRoot).FirstOrDefault();
            repository = Path.GetFullPath(candidateRoot);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string target = root ?? candidateRoot;
            throw new InvalidOperationException($"The repository root '{target}' is not accessible: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return new TestSourceTree(repository, SourceFilesByName(repository));
    }

    /// <inheritdoc />
    public string? ReadDeclaredNamespace(string filePath)
    {
        try
        {
            return TestClassPlanning.DeclaredNamespaceOf(File.ReadLines(filePath));
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
    internal static string DefaultRepositoryRoot()
    {
        string workingDirectory = Directory.GetCurrentDirectory();
        foreach (string start in new[] { workingDirectory, AppContext.BaseDirectory })
        {
            if (NearestRepositoryRoot(start) is { } found)
                return found;
        }

        return workingDirectory;
    }

    private static string? NearestRepositoryRoot(string start)
    {
        DirectoryInfo? current = new(Path.GetFullPath(start));
        while (current is not null)
        {
            // A worktree carries .git as a file and a checkout carries it as a directory.
            if (File.Exists(Path.Combine(current.FullName, ".git")) ||
                Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                Directory.Exists(Path.Combine(current.FullName, ".kyber-weave")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>Every <c>.cs</c> file under the root, keyed by its file name without the extension.</summary>
    /// <remarks>
    /// One walk serves every lookup, and the first file found for a name wins, so a class declared in a file
    /// of the same name resolves the same way on every run.
    /// </remarks>
    private static Dictionary<string, string> SourceFilesByName(string root)
    {
        Dictionary<string, string> byName = new(StringComparer.Ordinal);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = true
        };

        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
            {
                // A symbolic link is never followed: it can lead outside the repository, and the file
                // behind it is not this repository's source.
                if (entry.LinkTarget is not null)
                    continue;

                if (entry is DirectoryInfo child)
                {
                    if (!TestClassPlanning.GeneratedDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                        pending.Push(child.FullName);

                    continue;
                }

                if (!entry.Name.EndsWith(SourceExtension, StringComparison.Ordinal))
                    continue;

                byName.TryAdd(entry.Name[..^SourceExtension.Length], entry.FullName);
            }
        }

        return byName;
    }
}
