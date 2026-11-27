namespace KyberWeave.Core.Review;

/// <summary>
/// The pure rules that resolve the test classes covering a change, for the <c>{tests}</c> task-gate token: the name
/// convention, the declared <c>review.test-map</c>, and the check of every name back against the tree.
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
public static class TestClassPlanning
{
    private const string SourceExtension = ".cs";

    /// <summary>
    /// Directories whose contents are build output or third-party code. A walk over a repository root
    /// that descended into them would read a dependency's sources as this repository's tests.
    /// </summary>
    public static IReadOnlyList<string> GeneratedDirectories { get; } =
    [
        "bin", "obj", "node_modules", ".git", ".venv", "dist", "out", "coverage", "TestResults", "artifacts", "target"
    ];

    /// <summary>
    /// Resolves the fully qualified names of the test classes that cover <paramref name="changed"/> in an opened tree.
    /// </summary>
    /// <param name="tree">The repository root and the source files under it.</param>
    /// <param name="changed">The repository-relative changed paths, already validated.</param>
    /// <param name="declared">The host's <c>review.test-map</c>, as source glob to fully qualified class names, or null.</param>
    /// <param name="readDeclaredNamespace">Reads the namespace a source file declares; null when it declares none this reader can see.</param>
    /// <returns>The names in resolution order, without duplicates.</returns>
    public static IReadOnlyList<string> Resolve(
        TestSourceTree tree,
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declared,
        Func<string, string?> readDeclaredNamespace)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(readDeclaredNamespace);
        string repository = tree.Repository;
        IReadOnlyDictionary<string, string> sources = tree.SourcesByName;
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
                        if (IsRealClassName(repository, sources, name, readDeclaredNamespace, out string? real) && real == name)
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
                    string fqn = FullyQualifiedName(repository, file, candidate, readDeclaredNamespace);
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

    /// <summary>Whether the change, or the declared map, could name any class at all.</summary>
    /// <remarks>The walk behind the resolution is skipped when the answer is already no.</remarks>
    public static bool AnyPossibleCoverage(
        IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? declared)
    {
        ArgumentNullException.ThrowIfNull(changed);

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
        Func<string, string?> readDeclaredNamespace,
        out string? realName)
    {
        realName = null;
        string? simple = SimpleNameOf(name);
        if (simple is null || !sources.TryGetValue(simple, out string? file))
            return false;

        realName = FullyQualifiedName(root, file, simple, readDeclaredNamespace);
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

    /// <summary>The namespace a file declares, or null when it declares none this reader can see.</summary>
    /// <remarks>
    /// A source file declares its namespace on one line, before the first type it declares, so the first
    /// line that starts with the keyword answers it; a comment or a string that merely mentions the word
    /// does not start with it.
    /// </remarks>
    /// <param name="lines">The file's lines, read lazily so the walk stops at the declaration.</param>
    public static string? DeclaredNamespaceOf(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (string line in lines)
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

        return null;
    }

    /// <summary>The class's real name: the namespace it declares, or the one its directory implies.</summary>
    private static string FullyQualifiedName(string root, string filePath, string className, Func<string, string?> readDeclaredNamespace)
    {
        string namespaceName = readDeclaredNamespace(filePath) ?? NamespaceFromPath(root, filePath);
        return namespaceName.Length == 0 ? className : namespaceName + "." + className;
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
