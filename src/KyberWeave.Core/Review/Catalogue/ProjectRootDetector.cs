using System.ComponentModel;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Review.Catalogue;

/// <summary>
/// Finds the project roots of one family in a repository tree and gives each root a slug, so the
/// gate ids composed from those roots stay the same from one run to the next.
/// </summary>
/// <remarks>
/// Git decides what belongs to the repository whenever it can answer, so ignored paths never count.
/// The directory exclusions apply on top of that listing, so a committed vendor tree is never gated.
/// Everything is sorted by ordinal path before it is returned, so the same tree yields the same roots
/// whatever order the file system lists them in.
/// </remarks>
internal static class ProjectRootDetector
{
    /// <summary>A family with more roots than this has its gates written commented out by the caller.</summary>
    internal const int MaxRootsPerFamily = 8;

    private const string RepositoryRoot = ".";

    private const string RepositoryRootSlug = "root";

    private const string FallbackSlug = "project";

    private const int MaxSlugLength = 40;

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Dependency, build-output and tool-state directories that never hold a project of the repository.</summary>
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", "bin", "obj", "target", "vendor", "dist", "build",
        ".venv", "venv", "__pycache__", ".agent_scratchpad", ".kyber-weave"
    };

    /// <summary>Finds the roots of one family.</summary>
    /// <param name="repositoryRoot">The repository directory. Roots come back relative to it.</param>
    /// <param name="markers">File-name globs, matched case-sensitively against the file name only.</param>
    /// <param name="nesting">Whether every marker directory is a root, or only the outermost ones.</param>
    /// <param name="pair">
    /// When true, a root is a directory that holds every marker, and its marker path is the first listed one.
    /// </param>
    /// <param name="fallback">
    /// Optional file-name globs used only when exactly one file matches them and no marker matched anywhere.
    /// </param>
    public static ProjectRootSet Detect(
        string repositoryRoot,
        string[] markers,
        ProjectRootNesting nesting,
        bool pair = false,
        string[]? fallback = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(markers);

        if (!Directory.Exists(repositoryRoot))
            throw new DirectoryNotFoundException($"Repository root '{repositoryRoot}' does not exist.");

        string[] files = ListFiles(repositoryRoot)
            .Where(file => IsRegularFile(repositoryRoot, file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Dictionary<string, List<string>> namesByDirectory = new(StringComparer.Ordinal);
        foreach (string file in files)
        {
            string directory = DirectoryOf(file);
            if (!namesByDirectory.TryGetValue(directory, out List<string>? names))
            {
                names = [];
                namesByDirectory[directory] = names;
            }

            names.Add(NameOf(file));
        }

        bool anyMarkerMatched = namesByDirectory.Values.Any(names => names.Any(name => MatchesAny(markers, name)));

        List<Candidate> candidates = [];
        foreach (KeyValuePair<string, List<string>> entry in namesByDirectory.OrderBy(e => e.Key,
                     StringComparer.Ordinal))
        {
            string? markerPath = FindMarker(entry.Key, entry.Value, markers, pair);
            if (markerPath is not null)
                candidates.Add(new Candidate(entry.Key, markerPath));
        }

        // The fallback only guesses when nothing declared a root. Two candidate projects are
        // not a guess the operator can rely on, so more than one match gives no root at all.
        if (fallback is { Length: > 0 } && !anyMarkerMatched)
        {
            string[] hits = files.Where(file => MatchesAny(fallback, NameOf(file))).ToArray();
            if (hits.Length == 1)
                candidates.Add(new Candidate(DirectoryOf(hits[0]), hits[0]));
        }

        IEnumerable<Candidate> kept = candidates;
        if (nesting == ProjectRootNesting.Outermost)
        {
            HashSet<string> directories =
                candidates.Select(candidate => candidate.Directory).ToHashSet(StringComparer.Ordinal);
            kept = candidates.Where(candidate => !HasAncestorIn(candidate.Directory, directories));
        }

        HashSet<string> usedSlugs = new(StringComparer.Ordinal);
        List<ProjectRoot> roots = [];
        foreach (Candidate candidate in kept.OrderBy(c => c.Directory, StringComparer.Ordinal))
            roots.Add(new ProjectRoot(candidate.Directory, candidate.MarkerPath,
                ClaimSlug(candidate.Directory, usedSlugs)));

        return new ProjectRootSet(roots, roots.Count > MaxRootsPerFamily);
    }

    /// <summary>
    /// The marker path that makes <paramref name="directory"/> a root, or <see langword="null"/>. With
    /// <paramref name="pair"/> every marker must be present; otherwise the first listed marker that matches.
    /// </summary>
    private static string? FindMarker(string directory, IReadOnlyList<string> names, string[] markers, bool pair)
    {
        if (!pair)
        {
            foreach (string marker in markers)
            {
                string? hit = names.FirstOrDefault(name =>
                    FileSystemName.MatchesSimpleExpression(marker, name, ignoreCase: false));
                if (hit is not null)
                    return Join(directory, hit);
            }

            return null;
        }

        string? first = null;
        foreach (string marker in markers)
        {
            string? hit = names.FirstOrDefault(name =>
                FileSystemName.MatchesSimpleExpression(marker, name, ignoreCase: false));
            if (hit is null)
                return null;

            first ??= Join(directory, hit);
        }

        return first;
    }

    /// <summary>True when any ancestor directory of <paramref name="directory"/> is also a candidate.</summary>
    private static bool HasAncestorIn(string directory, HashSet<string> directories)
    {
        string current = directory;
        while (current != RepositoryRoot)
        {
            current = DirectoryOf(current);
            if (directories.Contains(current))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reserves a slug for <paramref name="directory"/>. A collision with a slug already reserved gets the
    /// first free <c>-2</c>, <c>-3</c> and so on, so the outcome depends only on the ordinal order.
    /// </summary>
    private static string ClaimSlug(string directory, HashSet<string> usedSlugs)
    {
        string baseSlug = SlugOf(directory);
        string slug = baseSlug;
        for (int suffix = 2; !usedSlugs.Add(slug); suffix++)
            slug = $"{baseSlug}-{suffix}";

        return slug;
    }

    /// <summary>
    /// The path lowercased, with each run of characters outside <c>[a-z0-9]</c> as one hyphen, trimmed,
    /// and cut to 40 characters. Falls back to a fixed word when nothing usable is left.
    /// </summary>
    private static string SlugOf(string directory)
    {
        if (directory == RepositoryRoot)
            return RepositoryRootSlug;

        StringBuilder slug = new(directory.Length);
        bool separatorPending = false;
        foreach (char raw in directory)
        {
            // ASCII-only folding: the slug is an identifier part, so no culture rule may decide it.
            char c = raw is >= 'A' and <= 'Z' ? (char)(raw + ('a' - 'A')) : raw;
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (separatorPending && slug.Length > 0)
                    slug.Append('-');

                separatorPending = false;
                slug.Append(c);
            }
            else
            {
                separatorPending = true;
            }
        }

        string text = slug.ToString();
        if (text.Length > MaxSlugLength)
            text = text[..MaxSlugLength].TrimEnd('-');

        return ConfigSlug.IsValid(text) ? text : FallbackSlug;
    }

    private static bool MatchesAny(string[] patterns, string name) =>
        patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false));

    /// <summary>Repository-relative files, from git when the directory is a work tree, otherwise from a walk.</summary>
    private static IReadOnlyList<string> ListFiles(string repositoryRoot)
    {
        IReadOnlyList<string>? fromGit = ListGitFiles(repositoryRoot);
        if (fromGit is not null)
            return fromGit.Where(file => !IsUnderExcludedDirectory(file)).ToArray();

        return WalkFiles(repositoryRoot);
    }

    /// <summary>
    /// Lists tracked and untracked-but-not-ignored files. Returns <see langword="null"/> when git is missing
    /// or the directory is not a work tree, so the caller walks the file system instead.
    /// </summary>
    private static IReadOnlyList<string>? ListGitFiles(string repositoryRoot)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            startInfo.ArgumentList.Add(argument);

        ProcessResult result;
        try
        {
            result = ProcessRunner.Run(startInfo, string.Empty, GitTimeout);
        }
        catch (Win32Exception)
        {
            return null;
        }

        if (result.ExitCode != 0)
            return null;

        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Walks one directory level at a time. Links are skipped rather than followed, so the walk cannot
    /// leave the repository, and excluded directories are never entered.
    /// </summary>
    private static IReadOnlyList<string> WalkFiles(string repositoryRoot)
    {
        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(string.Empty);

        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = true
        };

        while (pending.Count > 0)
        {
            string relative = pending.Pop();
            DirectoryInfo directory =
                new(relative.Length == 0 ? repositoryRoot : Path.Combine(repositoryRoot, relative));

            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", options))
            {
                if (entry.LinkTarget is not null)
                    continue;

                string child = relative.Length == 0 ? entry.Name : $"{relative}/{entry.Name}";
                if (entry is DirectoryInfo)
                {
                    if (!ExcludedDirectories.Contains(entry.Name))
                        pending.Push(child);
                }
                else
                {
                    files.Add(child);
                }
            }
        }

        return files;
    }

    private static bool IsUnderExcludedDirectory(string file)
    {
        string[] segments = file.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (ExcludedDirectories.Contains(segments[i]))
                return true;
        }

        return false;
    }

    /// <summary>A regular file that still exists. A deleted-but-tracked file and a link are not markers.</summary>
    private static bool IsRegularFile(string repositoryRoot, string file)
    {
        FileInfo info = new(Path.Combine(repositoryRoot, file));
        return info.Exists && info.LinkTarget is null;
    }

    private static string DirectoryOf(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? RepositoryRoot : path[..separator];
    }

    private static string NameOf(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string Join(string directory, string name) =>
        directory == RepositoryRoot ? name : $"{directory}/{name}";

    /// <summary>A directory that matched, and the marker path that proves it.</summary>
    private sealed record Candidate(string Directory, string MarkerPath);
}
