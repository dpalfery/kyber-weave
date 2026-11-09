namespace KyberWeave.Core.Review;

/// <summary>
/// Resolves the test classes that cover a change, for the <c>{tests}</c> task-gate token.
/// </summary>
/// <remarks>
/// <para>
/// A declared <c>review.test-map</c> entry is honoured first: an operator mapping a source area knows it
/// better than a file name does. The name convention then fills in what the map does not cover, by
/// mapping a changed file <c>Foo.cs</c> to the class <c>FooTests</c>.
/// </para>
/// <para>
/// Every name is checked back against the tree and is dropped when no source file names that class, or
/// when the name is not the one the class actually declares. A filter that matches nothing exits zero
/// and reads as a suite that ran and passed, which is the failure this resolution exists to prevent.
/// </para>
/// </remarks>
public static class TestClassMapping
{
    private const string SourceExtension = ".cs";

    /// <summary>
    /// Directories whose contents are build output or third-party code. A walk over a repository root
    /// that descended into them would read a dependency's sources as this repository's tests.
    /// </summary>
    private static readonly string[] GeneratedDirectories =
    [
        "bin", "obj", "node_modules", ".git", ".venv", "dist", "out", "coverage", "TestResults", "artifacts", "target"
    ];

    /// <summary>
    /// Resolves the fully qualified names of the test classes that cover <paramref name="changed"/>.
    /// </summary>
    /// <param name="root">
    /// The repository root the class files are looked up under. When null, the nearest enclosing
    /// repository of the current process is used, so a caller that holds no root can still ask.
    /// </param>
    /// <param name="changed">The repository-relative changed paths, already validated.</param>
    /// <param name="declared">
    /// The host's <c>review.test-map</c>, as source glob to fully qualified class names, or null when
    /// the host declares none.
    /// </param>
    /// <returns>
    /// The names in resolution order, without duplicates. Empty means nothing covers the change, which
    /// the caller reports as a skip rather than running a filter that selects nothing.
    /// </returns>
    public static IReadOnlyList<string> Resolve(
        string? root,
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declared = null)
    {
        ArgumentNullException.ThrowIfNull(changed);

        if (!AnyPossibleCoverage(changed, declared))
            return [];

        string repository;
        try
        {
            string candidate = root ?? DefaultRepositoryRoot();
            if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate))
                return [];

            repository = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        IReadOnlyDictionary<string, string> sources = SourceFilesByName(repository);
        List<string> resolved = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        if (declared is not null)
        {
            foreach (string path in changed)
            {
                string normalized = Normalize(path);
                foreach (KeyValuePair<string, IReadOnlyList<string>> entry in declared)
                {
                    if (!PathGlob.IsMatch(entry.Key, normalized))
                        continue;

                    foreach (string name in entry.Value)
                    {
                        if (IsRealClassName(repository, sources, name, out string? real) && real == name)
                            Add(name);
                    }
                }
            }
        }

        foreach (string path in changed)
        {
            foreach (string candidate in CandidateClassNames(path))
            {
                if (sources.TryGetValue(candidate, out string? file))
                {
                    string fqn = FullyQualifiedName(repository, file, candidate);
                    if (fqn.Contains('.', StringComparison.Ordinal))
                        Add(fqn);
                }
            }
        }

        return resolved;

        void Add(string name)
        {
            if (name.Length > 0 && seen.Add(name))
                resolved.Add(name);
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

    /// <summary>Whether the change, or the declared map, could name any class at all.</summary>
    /// <remarks>The walk behind <see cref="Resolve"/> is skipped when the answer is already no.</remarks>
    private static bool AnyPossibleCoverage(
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declared)
    {
        foreach (string path in changed)
        {
            string normalized = Normalize(path);
            if (normalized.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase))
                return true;

            if (declared is null)
                continue;

            foreach (string pattern in declared.Keys)
            {
                if (PathGlob.IsMatch(pattern, normalized))
                    return true;
            }
        }

        return false;
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

    /// <summary>
    /// The class names one changed file could name: its own class when it is a test file, and the class the
    /// name convention pairs with it otherwise.
    /// </summary>
    private static string[] CandidateClassNames(string changedPath)
    {
        string normalized = Normalize(changedPath);
        if (!normalized.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase))
            return [];

        string stem = Path.GetFileNameWithoutExtension(normalized);
        if (stem.Length == 0 || stem == ".")
            return [];

        return IsTestFile(normalized, stem) ? [stem] : [stem + "Tests"];
    }

    /// <summary>
    /// Whether the file names a test class itself, so its own stem is the class rather than a source to pair.
    /// </summary>
    private static bool IsTestFile(string normalizedPath, string stem)
    {
        if (stem.EndsWith("Tests", StringComparison.Ordinal))
            return true;

        foreach (string segment in normalizedPath.Split('/'))
        {
            if (string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "test", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="name"/> names a class that exists, with the name the class actually has.
    /// </summary>
    /// <remarks>
    /// The class is found by the file named after it, and its namespace is read from that file. A name whose
    /// namespace is not the declared one is not the class's name, so it is not emitted: the filter would run
    /// and select nothing.
    /// </remarks>
    private static bool IsRealClassName(
        string root,
        IReadOnlyDictionary<string, string> sources,
        string name,
        out string? realName)
    {
        realName = null;
        string? simple = SimpleNameOf(name);
        if (simple is null || !sources.TryGetValue(simple, out string? file))
            return false;

        realName = FullyQualifiedName(root, file, simple);
        return true;
    }

    private static string? SimpleNameOf(string fullyQualifiedName)
    {
        if (fullyQualifiedName.Length == 0)
            return null;

        int lastSeparator = fullyQualifiedName.LastIndexOf('.');
        string simple = lastSeparator < 0 ? fullyQualifiedName : fullyQualifiedName[(lastSeparator + 1)..];
        return simple.Length == 0 ? null : simple;
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
                    if (!GeneratedDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
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

    /// <summary>The class's real name: the namespace it declares, or the one its directory implies.</summary>
    private static string FullyQualifiedName(string root, string filePath, string className)
    {
        string namespaceName = DeclaredNamespace(filePath) ?? NamespaceFromPath(root, filePath);
        return namespaceName.Length == 0 ? className : namespaceName + "." + className;
    }

    /// <summary>The namespace a file declares, or null when it declares none this reader can see.</summary>
    /// <remarks>
    /// A source file declares its namespace on one line, before the first type it declares, so the first
    /// line that starts with the keyword answers it; a comment or a string that merely mentions the word
    /// does not start with it. A file that cannot be read yields null rather than an exception, because the
    /// class is still known to exist and only its namespace is unknown.
    /// </remarks>
    private static string? DeclaredNamespace(string filePath)
    {
        try
        {
            foreach (string line in File.ReadLines(filePath))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("namespace", StringComparison.Ordinal))
                    continue;

                string rest = trimmed["namespace".Length..];
                if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
                    continue;

                int end = rest.AsSpan().IndexOfAny(';', '{');
                string name = (end < 0 ? rest : rest[..end]).Trim();
                return name.Length == 0 ? null : name;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The namespace a file's own directory implies, for a file that declares none.</summary>
    /// <remarks>
    /// <c>tests/KyberWeave.Tests/ATests.cs</c> implies <c>KyberWeave.Tests</c>: the directory below the
    /// walk's top level names the project's root namespace, and each directory under it adds a segment.
    /// </remarks>
    private static string NamespaceFromPath(string root, string filePath)
    {
        string relative = Path.GetRelativePath(root, filePath);
        string[] segments = Normalize(relative).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
            return string.Empty;

        return string.Join('.', segments[1..^1]);
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/');
    }
}
