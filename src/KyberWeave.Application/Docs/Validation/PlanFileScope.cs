using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Processes;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Keeps, from the entries a task's text yields as its files, only those that are repository paths. A plan's prose also
/// holds routes, URLs, call expressions and namespaces in backticks, and a file list that carries them feeds wrong
/// scopes into the in-flight overlap check and the diff attribution. The judgment is made from the repository, not from
/// more patterns: an entry is a file when it exists, or when its top-level folder exists and its extension is one the
/// repository already tracks (so a file the task creates, even in a new folder, still counts).
/// </summary>
internal sealed partial class PlanFileScope
{
    private static readonly string[] SkippedDirectories = [".git", "node_modules", "bin", "obj", ".agent_scratchpad"];

    private readonly string? _root;
    private readonly IRepositoryFileStore _files;
    private readonly IBoundedProcessExecutor _process;
    private readonly Lazy<HashSet<string>?> _extensions;

    /// <summary>Creates the scope for one repository.</summary>
    /// <param name="repositoryRoot">The repository root, or null when unknown; without it only entries that cannot be paths are dropped.</param>
    /// <param name="files">Probes the repository's files and folders.</param>
    /// <param name="process">Runs <c>git ls-files</c> to read the extensions the repository tracks.</param>
    public PlanFileScope(string? repositoryRoot, IRepositoryFileStore? files, IBoundedProcessExecutor? process)
    {
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            ArgumentNullException.ThrowIfNull(files);
            ArgumentNullException.ThrowIfNull(process);
        }

        _files = files!;
        _process = process!;
        _root = string.IsNullOrWhiteSpace(repositoryRoot) ? null : Path.GetFullPath(repositoryRoot);
        _extensions = new Lazy<HashSet<string>?>(ReadExtensions);
    }

    /// <summary>Splits <paramref name="entries"/> into the kept files and the dropped text.</summary>
    public (IReadOnlyList<string> Kept, IReadOnlyList<string> Dropped) Filter(IReadOnlyList<string> entries)
    {
        List<string> kept = [];
        List<string> dropped = [];
        foreach (string entry in entries)
        {
            (IsPath(entry) ? kept : dropped).Add(entry);
        }

        return (kept, dropped);
    }

    private bool IsPath(string entry)
    {
        string text = entry.Trim();
        if (text.Length == 0 || text.Contains("://", StringComparison.Ordinal) || text.Contains('(', StringComparison.Ordinal)
            || text.Contains(')', StringComparison.Ordinal) || text.Any(char.IsWhiteSpace) || SchemeRegex().IsMatch(text)
            || text.Any(character => character is '\'' or '"' or '`' or '<' or '>' or '{' or '}' or '|' or '$'))
        {
            return false;
        }

        if (_root is null)
        {
            return true;
        }

        string relative = text.TrimEnd('/').Replace('\\', '/');
        if (relative.StartsWith('/') || relative.Length == 0)
        {
            return false;
        }

        int glob = relative.IndexOfAny(['*', '?']);
        string literal = glob < 0 ? relative : relative[..Math.Max(0, relative.LastIndexOf('/', glob))];
        string full = Path.Combine(_root, literal.Replace('/', Path.DirectorySeparatorChar));
        if (glob >= 0)
        {
            return literal.Length > 0 && _files.DirectoryExists(full);
        }

        if (_files.FileExists(full) || _files.DirectoryExists(full))
        {
            return true;
        }

        string firstSegment = relative.Split('/')[0];
        string extension = Path.GetExtension(relative);
        bool topLevelExists = _files.DirectoryExists(Path.Combine(_root, firstSegment)) || (!relative.Contains('/', StringComparison.Ordinal) && _files.FileExists(Path.Combine(_root, firstSegment)));
        return extension.Length > 1
               && (relative.Contains('/', StringComparison.Ordinal) ? topLevelExists : true)
               && _extensions.Value?.Contains(extension) != false;
    }

    private HashSet<string>? ReadExtensions()
    {
        if (_root is null)
        {
            return null;
        }

        HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            ProcessStartInfo start = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = _root };
            foreach (string argument in new[] { "-C", _root, "ls-files", "-z" })
            {
                start.ArgumentList.Add(argument);
            }

            ProcessResult result = _process.Run(start, string.Empty, TimeSpan.FromSeconds(20));
            if (result.ExitCode == 0 && result.StandardOutput.Length > 0)
            {
                foreach (string path in result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    AddExtension(extensions, path);
                }

                return extensions;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or TimeoutException or IOException)
        {
            // Not a git checkout, or git is absent: the files on disk say which extensions the repository has.
        }

        foreach (string path in EnumerateFiles(_root, 0))
        {
            AddExtension(extensions, path);
        }

        return extensions.Count > 0 ? extensions : null;
    }

    private static void AddExtension(HashSet<string> extensions, string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length > 1)
        {
            extensions.Add(extension);
        }
    }

    private IEnumerable<string> EnumerateFiles(string directory, int depth)
    {
        if (depth > 8)
        {
            yield break;
        }

        string[] files;
        string[] directories;
        try
        {
            files = _files.GetFiles(directory);
            directories = _files.GetDirectories(directory);
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            yield break;
        }

        foreach (string file in files)
        {
            yield return file;
        }

        foreach (string child in directories.Where(child => !SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.Ordinal)))
        {
            foreach (string file in EnumerateFiles(child, depth + 1))
            {
                yield return file;
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*:(?!\\|/)", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();
}
