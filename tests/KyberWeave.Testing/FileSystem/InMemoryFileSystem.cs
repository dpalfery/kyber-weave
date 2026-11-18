using System.Text;
using KyberWeave.Contracts.Repositories;
using FileSystemName = System.IO.Enumeration.FileSystemName;

namespace KyberWeave.Tests.FileSystem;

/// <summary>An <see cref="IWritableFileSystem"/> held entirely in memory, so a unit test needs no disk.</summary>
/// <remarks>
/// <para>
/// The fake reproduces the semantics the product relies on: an atomic rename that may replace its
/// destination, an exclusive create, case-preserving names with a case-insensitive or a
/// case-sensitive lookup chosen per instance, and per directory for a directory that asks for
/// exact names, file and directory links resolved one hop or to the final target, deletion that
/// removes a link instead of following it, enumeration with and
/// without following links, length, attributes and a last-write time taken from the injected
/// <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// Paths are absolute and live under a virtual root (<c>/</c> on a Unix-like platform,
/// <c>C:\</c> on Windows). Nothing here touches the real file system, the environment or the
/// clock.
/// </para>
/// <para>
/// Fault injection is the reason the fake exists for crash-safety tests: <see cref="FailAt"/>,
/// <see cref="KillAt"/>, <see cref="FailWhen"/> and <see cref="KillWhen"/> make an operation throw
/// or stop the fake as a killed process would, leaving the tree for the test to inspect.
/// </para>
/// </remarks>
public sealed class InMemoryFileSystem : IWritableFileSystem
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", ".git", ".venv", "dist", "out", "coverage", "TestResults", "artifacts", "target"
    };

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly UTF8Encoding PlainUtf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _rootPath;
    private readonly StringComparer _names;
    private readonly TimeProvider _time;
    private readonly Node _root;
    private readonly List<FileSystemFault> _faults = [];

    /// <summary>Creates an empty file system.</summary>
    /// <param name="timeProvider">The clock its last-write times come from; the system clock when null.</param>
    /// <param name="caseSensitive">True for Linux semantics; false for the case-insensitive, case-preserving APFS and NTFS behaviour.</param>
    public InMemoryFileSystem(TimeProvider? timeProvider = null, bool caseSensitive = false)
    {
        _time = timeProvider ?? TimeProvider.System;
        _names = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _rootPath = OperatingSystem.IsWindows() ? @"C:\" : "/";
        _root = new Node(_rootPath, parent: null, isDirectory: true, _names, Now);
        CaseSensitive = caseSensitive;
    }

    /// <summary>Gets whether names are compared case-sensitively in this instance.</summary>
    public bool CaseSensitive { get; }

    /// <summary>Gets how many seam calls this instance has seen. Fault ordinals count this number.</summary>
    public int OperationCount { get; private set; }

    /// <summary>Gets whether a <see cref="FileSystemFaultKind.Kill"/> fault has stopped the fake.</summary>
    public bool IsKilled { get; private set; }

    /// <summary>Gets the fault rules, in the order they are checked.</summary>
    public IReadOnlyList<FileSystemFault> Faults => _faults;

    /// <summary>Adds a fault rule.</summary>
    /// <param name="fault">The rule.</param>
    public void AddFault(FileSystemFault fault) => _faults.Add(fault ?? throw new ArgumentNullException(nameof(fault)));

    /// <summary>Fails at one operation.</summary>
    /// <param name="operationNumber">The one-based operation number.</param>
    /// <param name="failure">The exception to throw; an <see cref="IOException"/> when null.</param>
    public void FailAt(int operationNumber, Exception? failure = null) => _faults.Add(FileSystemFault.FailAt(operationNumber, failure));

    /// <summary>Stops the fake at one operation as a killed process would.</summary>
    /// <param name="operationNumber">The one-based operation number.</param>
    public void KillAt(int operationNumber) => _faults.Add(FileSystemFault.KillAt(operationNumber));

    /// <summary>Fails every operation the predicate matches.</summary>
    /// <param name="predicate">Chooses the operations to fail.</param>
    /// <param name="failure">The exception to throw; an <see cref="IOException"/> when null.</param>
    public void FailWhen(Func<FileSystemOperation, bool> predicate, Exception? failure = null) =>
        _faults.Add(FileSystemFault.FailWhen(predicate, failure));

    /// <summary>Stops the fake at the first operation the predicate matches.</summary>
    /// <param name="predicate">Chooses the operation.</param>
    public void KillWhen(Func<FileSystemOperation, bool> predicate) => _faults.Add(FileSystemFault.KillWhen(predicate));

    /// <summary>Removes every fault and clears the killed state, as restarting a process does; the tree survives.</summary>
    public void ClearFaults()
    {
        _faults.Clear();
        IsKilled = false;
    }

    /// <summary>Sets a path's attributes, so a test can make a file read-only.</summary>
    /// <param name="path">The absolute path.</param>
    /// <param name="attributes">The new attributes.</param>
    /// <exception cref="FileNotFoundException">Nothing exists at the path.</exception>
    public void SetAttributes(string path, FileAttributes attributes) =>
        ResolveRequired(path).Attributes = attributes;

    /// <inheritdoc />
    public bool FileExists(string path)
    {
        Enter(nameof(FileExists), path);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return Follow(Resolve(path, followFinal: false)) is { IsDirectory: false };
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        Enter(nameof(DirectoryExists), path);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return Follow(Resolve(path, followFinal: false)) is { IsDirectory: true };
    }

    /// <inheritdoc />
    public bool PathExists(string path)
    {
        Enter(nameof(PathExists), path);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return Follow(Resolve(path, followFinal: false)) is not null;
    }

    /// <inheritdoc />
    public string ReadAllText(string path)
    {
        Enter(nameof(ReadAllText), path);
        Node node = ResolveRequiredFile(path);
        using MemoryStream stream = new(node.Content, writable: false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <inheritdoc />
    public byte[] ReadAllBytes(string path)
    {
        Enter(nameof(ReadAllBytes), path);
        return [.. ResolveRequiredFile(path).Content];
    }

    /// <inheritdoc />
    public IEnumerable<string> ReadLines(string path)
    {
        Enter(nameof(ReadLines), path);
        return ReadLinesCore(path);
    }

    /// <inheritdoc />
    public Stream OpenRead(string path)
    {
        Enter(nameof(OpenRead), path);
        return new MemoryStream(ResolveRequiredFile(path).Content, writable: false);
    }

    /// <inheritdoc />
    public long GetLength(string path)
    {
        Enter(nameof(GetLength), path);
        return ResolveRequiredFile(path).Content.Length;
    }

    /// <inheritdoc />
    public DateTime GetLastWriteTimeUtc(string path)
    {
        Enter(nameof(GetLastWriteTimeUtc), path);
        return Follow(Resolve(path, followFinal: false))?.LastWriteTimeUtc ?? DateTime.FromFileTimeUtc(0);
    }

    /// <inheritdoc />
    public FileAttributes GetAttributes(string path)
    {
        Enter(nameof(GetAttributes), path);
        Node node = Resolve(path, followFinal: false) ?? throw Missing(path);
        Node final = Follow(node) ?? throw Missing(path);
        return node.LinkTarget is null ? final.Attributes : final.Attributes | FileAttributes.ReparsePoint;
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        EnumerateFiles(directory, searchPattern, recursive, followLinks: true);

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks)
    {
        Enter(nameof(EnumerateFiles), directory);
        return EnumerateFilesCore(directory, searchPattern, recursive, followLinks, []);
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        Enter(nameof(EnumerateDirectories), directory);
        return EnumerateDirectoriesCore(directory);
    }

    /// <inheritdoc />
    public IEnumerable<FileSystemEntry> EnumerateEntries(string directory)
    {
        Enter(nameof(EnumerateEntries), directory);
        return EnumerateEntriesCore(directory);
    }

    /// <inheritdoc />
    public string? GetLinkTarget(string path)
    {
        Enter(nameof(GetLinkTarget), path);
        Node? node = Resolve(path, followFinal: false);
        return node?.LinkTarget;
    }

    /// <inheritdoc />
    public string? GetFinalLinkTarget(string path)
    {
        Enter(nameof(GetFinalLinkTarget), path);
        Node? node = Resolve(path, followFinal: false);
        if (node?.LinkTarget is null)
        {
            return null;
        }

        Node? target = Follow(node);
        return target is null ? null : CanonicalPath(target);
    }

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        Enter(nameof(CreateDirectory), path);
        CreateDirectoryCore(Full(path), caseSensitiveNames: null);
    }

    /// <summary>Gives the directory the name rule the caller names, creating it when it is not there.</summary>
    /// <param name="path">The absolute path of the directory.</param>
    /// <param name="caseSensitiveNames">
    /// True to match this directory's children exactly, as a Windows directory with case sensitivity turned on does
    /// even when the volume folds names; false for the volume's own rule.
    /// </param>
    /// <remarks>
    /// <para>
    /// The setting belongs to this directory alone and is not inherited by the directories created beneath it, so it
    /// models the per-directory flag rather than a second volume-wide rule. Asking an existing directory for a rule
    /// it does not have changes it, as turning the flag on and off does, and asking for folded names when two of them
    /// differ only in case is refused: one entry cannot carry both.
    /// </para>
    /// <para>
    /// <see cref="CreateDirectory(string)" /> leaves an existing directory's rule alone, so a caller that only needs
    /// the directory never disturbs the one a test set up.
    /// </para>
    /// </remarks>
    public void CreateDirectory(string path, bool caseSensitiveNames)
    {
        Enter(nameof(CreateDirectory), path);
        CreateDirectoryCore(Full(path), caseSensitiveNames);
    }

    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        Enter(nameof(WriteAllText), path);
        WriteContent(path, PlainUtf8.GetBytes(contents));
    }

    /// <inheritdoc />
    public void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Enter(nameof(WriteAllBytes), path);
        WriteContent(path, [.. bytes]);
    }

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        Enter(nameof(WriteAllTextAtomic), path);
        WriteAtomic(path, PlainUtf8.GetBytes(contents));
    }

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Enter(nameof(WriteAllBytesAtomic), path);
        WriteAtomic(path, [.. bytes]);
    }

    /// <inheritdoc />
    public void WriteAllTextAtomic(string path, string contents, bool flushToDisk) =>
        WriteAllTextAtomic(path, contents);

    /// <inheritdoc />
    public void WriteAllBytesAtomic(string path, byte[] bytes, bool flushToDisk) =>
        WriteAllBytesAtomic(path, bytes);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath) => MoveFile(sourcePath, destinationPath, overwrite: false);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        Enter(nameof(MoveFile), sourcePath, destinationPath);
        Node source = Resolve(sourcePath, followFinal: false) ?? throw Missing(sourcePath);
        if (source.IsDirectory && source.LinkTarget is null)
        {
            throw new IOException($"The source '{sourcePath}' is a directory, not a file.");
        }

        string destination = Full(destinationPath);
        string? parentPath = Path.GetDirectoryName(destination);
        Node parent = (parentPath is null ? null : ResolveFull(parentPath, followFinal: true))
            ?? throw new DirectoryNotFoundException($"Could not find a part of the path '{destinationPath}'.");
        if (!parent.IsDirectory)
        {
            throw new DirectoryNotFoundException($"Could not find a part of the path '{destinationPath}'.");
        }

        string name = Path.GetFileName(destination);
        Node? existing = Child(parent, name);
        if (existing is not null)
        {
            if (ReferenceEquals(existing, source))
            {
                if (string.Equals(name, source.Name, StringComparison.Ordinal))
                {
                    throw new IOException($"The source and destination are the same file: '{sourcePath}'.");
                }

                // A case-only rename is a real rename on APFS and NTFS: the same file takes the destination spelling.
            }
            else
            {
                if (!overwrite)
                {
                    throw new IOException($"The file '{destinationPath}' already exists.");
                }

                if (existing.IsDirectory && existing.LinkTarget is null)
                {
                    throw new IOException($"The destination '{destinationPath}' is a directory.");
                }

                Remove(existing);
            }
        }

        Remove(source);
        source.Parent = parent;
        source.Name = name;
        parent.Children[name] = source;
    }

    /// <inheritdoc />
    public void CreateNewFile(string path)
    {
        Enter(nameof(CreateNewFile), path);
        Node parent = ResolveParent(path, out string name);
        if (Child(parent, name) is not null)
        {
            throw new IOException($"The file '{path}' already exists.");
        }

        parent.Children[name] = NewFile(name, parent, []);
    }

    /// <inheritdoc />
    public bool TryCreateNewFile(string path)
    {
        Enter(nameof(TryCreateNewFile), path);
        Node parent = ResolveParent(path, out string name);
        if (Child(parent, name) is not null)
        {
            return false;
        }

        parent.Children[name] = NewFile(name, parent, []);
        return true;
    }

    /// <inheritdoc />
    public Stream OpenWriteNew(string path)
    {
        Enter(nameof(OpenWriteNew), path);
        Node parent = ResolveParent(path, out string name);
        if (Child(parent, name) is not null)
        {
            throw new IOException($"The file '{path}' already exists.");
        }

        Node file = NewFile(name, parent, []);
        parent.Children[name] = file;
        return new NewFileStream(file, _time);
    }

    /// <inheritdoc />
    public Stream OpenExclusive(string path)
    {
        Enter(nameof(OpenExclusive), path);
        Node file = OpenOrCreateForStream(path);
        if (file.ExclusiveHeld || file.AppendersOpen > 0)
        {
            throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
        }

        file.ExclusiveHeld = true;
        return new ExclusiveStream(file, _time);
    }

    /// <inheritdoc />
    public Stream OpenAppendShared(string path)
    {
        Enter(nameof(OpenAppendShared), path);
        Node file = OpenOrCreateForStream(path);
        if (file.ExclusiveHeld)
        {
            throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
        }

        file.AppendersOpen++;
        return new AppendStream(file, _time);
    }

    /// <inheritdoc />
    public void MoveDirectory(string sourcePath, string destinationPath)
    {
        Enter(nameof(MoveDirectory), sourcePath, destinationPath);
        Node source = Resolve(sourcePath, followFinal: false)
            ?? throw new DirectoryNotFoundException($"Could not find a part of the path '{sourcePath}'.");
        if (!source.IsDirectory || source.LinkTarget is not null)
        {
            throw new IOException($"The source '{sourcePath}' is not a directory.");
        }

        string destination = Full(destinationPath);
        string? parentPath = Path.GetDirectoryName(destination);
        Node parent = (parentPath is null ? null : ResolveFull(parentPath, followFinal: true))
            ?? throw new DirectoryNotFoundException($"Could not find a part of the path '{destinationPath}'.");
        if (!parent.IsDirectory)
        {
            throw new DirectoryNotFoundException($"Could not find a part of the path '{destinationPath}'.");
        }

        string name = Path.GetFileName(destination);
        if (Child(parent, name) is not null)
        {
            throw new IOException($"The path '{destinationPath}' already exists.");
        }

        for (Node? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, source))
            {
                throw new IOException($"The directory '{sourcePath}' cannot move into itself.");
            }
        }

        Remove(source);
        source.Parent = parent;
        source.Name = name;
        parent.Children[name] = source;
    }

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        Enter(nameof(DeleteFile), path);
        Node? node = Resolve(path, followFinal: false);
        if (node is null)
        {
            return;
        }

        Node? final = Follow(node);
        if (final is { IsDirectory: true })
        {
            throw new UnauthorizedAccessException($"The path '{path}' is a directory, not a file.");
        }

        Remove(node);
    }

    /// <inheritdoc />
    public void DeleteTreeNoFollow(string directoryPath)
    {
        Enter(nameof(DeleteTreeNoFollow), directoryPath);
        Node? node = Resolve(directoryPath, followFinal: false);
        if (node is not null)
        {
            DeleteNodeNoFollow(node);
        }
    }

    /// <inheritdoc />
    public void CreateFileLink(string linkPath, string targetPath) => CreateLink(linkPath, targetPath, namesDirectory: false);

    /// <inheritdoc />
    public void CreateDirectoryLink(string linkPath, string targetPath) => CreateLink(linkPath, targetPath, namesDirectory: true);

    /// <inheritdoc />
    public string CreateTemporaryDirectory(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (Path.IsPathRooted(prefix) ||
            prefix.Contains('/', StringComparison.Ordinal) ||
            prefix.Contains('\\', StringComparison.Ordinal) ||
            prefix.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The prefix names a path rather than a directory name, so the directory would not stay in the temporary directory.",
                nameof(prefix));
        }

        Enter(nameof(CreateTemporaryDirectory), prefix);
        string root = Path.Combine(_rootPath, "kw-memfs-tmp");
        CreateDirectoryCore(root, caseSensitiveNames: null);

        string path;
        do
        {
            path = Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));
        }
        while (ResolveFull(path, followFinal: false) is not null);

        CreateDirectoryCore(path, caseSensitiveNames: null);
        return path;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private static FileNotFoundException Missing(string path) =>
        new($"Could not find file '{path}'.", path);

    private void Enter(string name, string? path, string? destinationPath = null)
    {
        if (IsKilled)
        {
            throw new InMemoryFileSystemKilledException($"The in-memory file system was already killed at operation {OperationCount}.");
        }

        int number = ++OperationCount;
        FileSystemOperation operation = new(name, path, destinationPath);
        foreach (FileSystemFault fault in _faults)
        {
            if (!fault.Matches(number, operation))
            {
                continue;
            }

            if (fault.Kind == FileSystemFaultKind.Kill)
            {
                IsKilled = true;
                throw new InMemoryFileSystemKilledException(
                    $"The in-memory file system was killed at operation {number} ({name} '{path}').");
            }

            throw fault.Failure ?? new IOException($"The in-memory file system failed at operation {number} ({name} '{path}').");
        }
    }

    private string Full(string path) => Path.GetFullPath(path, _rootPath);

    private string CanonicalPath(Node node)
    {
        if (node.Parent is null)
        {
            return _rootPath;
        }

        List<string> names = [];
        for (Node? current = node; current.Parent is not null; current = current.Parent)
        {
            names.Add(current.Name);
        }

        names.Reverse();
        return Path.Combine([_rootPath, .. names]);
    }

    private Node? Resolve(string path, bool followFinal) => ResolveFull(Full(path), followFinal);

    private Node? ResolveFull(string fullPath, bool followFinal)
    {
        string relative = Path.GetRelativePath(_rootPath, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        string[] segments = relative == "." ? [] : relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        Node current = _root;
        foreach (string segment in segments)
        {
            if (current.LinkTarget is not null)
            {
                Node? linked = Follow(current);
                if (linked is null)
                {
                    return null;
                }

                current = linked;
            }

            if (!current.IsDirectory)
            {
                return null;
            }

            Node? child = Child(current, segment);
            if (child is null)
            {
                return null;
            }

            current = child;
        }

        return followFinal ? Follow(current) : current;
    }

    private Node? Follow(Node? node)
    {
        int hops = 0;
        while (node?.LinkTarget is not null)
        {
            if (++hops > 64)
            {
                throw new IOException($"Too many levels of symbolic links near '{CanonicalPath(node)}'.");
            }

            string parentPath = node.Parent is null ? _rootPath : CanonicalPath(node.Parent);
            string target = Path.GetFullPath(node.LinkTarget, parentPath);
            node = ResolveFull(target, followFinal: false);
        }

        return node;
    }

    private Node? Child(Node directory, string name) =>
        directory.Children.TryGetValue(name, out Node? child) ? child : null;

    private IEnumerable<Node> Ordered(Node directory) =>
        directory.Children.Values.OrderBy(child => child.Name, StringComparer.Ordinal);

    private bool IsDirectoryEntry(Node node)
    {
        if (node.LinkTarget is null)
        {
            return node.IsDirectory;
        }

        try
        {
            return Follow(node)?.IsDirectory ?? node.LinkNamesDirectory;
        }
        catch (IOException)
        {
            return node.LinkNamesDirectory;
        }
    }

    private Node ResolveParent(string path, out string name)
    {
        string full = Full(path);
        string? parentPath = Path.GetDirectoryName(full)
            ?? throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");
        Node? parent = ResolveFull(parentPath, followFinal: true);
        if (parent is not { IsDirectory: true })
        {
            throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");
        }

        name = Path.GetFileName(full);
        return parent;
    }

    private Node ResolveRequired(string path) =>
        Resolve(path, followFinal: false) ?? throw Missing(path);

    private Node ResolveRequiredFile(string path)
    {
        Node? node = Resolve(path, followFinal: false);
        if (node is null)
        {
            string full = Full(path);
            string? parentPath = Path.GetDirectoryName(full);
            if (parentPath is not null && ResolveFull(parentPath, followFinal: true) is null)
            {
                throw new DirectoryNotFoundException($"Could not find a part of the path '{path}'.");
            }

            throw Missing(path);
        }

        Node final = Follow(node) ?? throw Missing(path);
        if (final.IsDirectory)
        {
            throw new UnauthorizedAccessException($"The path '{path}' is a directory, not a file.");
        }

        return final;
    }

    private Node NewDirectory(string name, Node parent, bool caseSensitiveNames = false) =>
        new(name, parent, isDirectory: true, _names, Now, caseSensitiveNames);

    private Node OpenOrCreateForStream(string path)
    {
        Node parent = ResolveParent(path, out string name);
        Node? existing = Child(parent, name);
        if (existing is null)
        {
            Node created = NewFile(name, parent, []);
            parent.Children[name] = created;
            return created;
        }

        Node file = Follow(existing) ?? throw Missing(path);
        return file.IsDirectory
            ? throw new UnauthorizedAccessException($"The path '{path}' is a directory, not a file.")
            : file;
    }

    private Node NewFile(string name, Node parent, byte[] content) =>
        new(name, parent, isDirectory: false, _names, Now) { Content = content };

    private void CreateDirectoryCore(string fullPath, bool? caseSensitiveNames)
    {
        string relative = Path.GetRelativePath(_rootPath, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new IOException($"The directory '{fullPath}' is outside the in-memory root.");
        }

        string[] segments = relative == "." ? [] : relative.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        Node current = _root;
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (current.LinkTarget is not null)
            {
                current = Follow(current)
                    ?? throw new IOException($"The directory '{fullPath}' passes through a dangling link.");
            }

            if (!current.IsDirectory)
            {
                throw new IOException($"A file is in the way of the directory '{fullPath}'.");
            }

            bool lastSegment = index == segments.Length - 1;
            Node? next = Child(current, segment);
            if (next is null)
            {
                Node created = NewDirectory(segment, current, lastSegment && caseSensitiveNames is true);
                current.Children[segment] = created;
                next = created;
            }
            else if (lastSegment && caseSensitiveNames is not null)
            {
                ApplyNameRule(next, caseSensitiveNames.Value);
            }

            current = next;
        }

        if (current.LinkTarget is not null)
        {
            current = Follow(current)
                ?? throw new IOException($"The directory '{fullPath}' names a dangling link.");
        }

        if (!current.IsDirectory)
        {
            throw new IOException($"A file is in the way of the directory '{fullPath}'.");
        }
    }

    /// <summary>
    ///     Gives an existing directory another name rule by rebuilding the lookup its children live in, which is the
    ///     only way a comparer changes. The tree is untouched when two names would fold together.
    /// </summary>
    private void ApplyNameRule(Node directory, bool caseSensitiveNames)
    {
        if (!directory.IsDirectory || directory.LinkTarget is not null)
        {
            throw new IOException(
                $"'{CanonicalPath(directory)}' is not a directory whose name rule this file system can change.");
        }

        StringComparer rule = caseSensitiveNames ? StringComparer.Ordinal : _names;
        if (directory.Children.Comparer.Equals(rule))
        {
            return;
        }

        Dictionary<string, Node> rekeyed = new(rule);
        foreach (string name in directory.Children.Keys)
        {
            if (!rekeyed.TryAdd(name, directory.Children[name]))
            {
                throw new IOException(
                    $"The directory '{CanonicalPath(directory)}' holds names that fold to '{name}', " +
                    "so it cannot match names by the volume's rule.");
            }
        }

        directory.Children = rekeyed;
    }

    private void WriteContent(string path, byte[] content)
    {
        Node? node = Resolve(path, followFinal: false);
        if (node is not null)
        {
            Node? final = Follow(node);
            if (final is not null)
            {
                SetContent(final, content);
                return;
            }

            string parentPath = node.Parent is null ? _rootPath : CanonicalPath(node.Parent);
            string target = Path.GetFullPath(node.LinkTarget!, parentPath);
            WriteAtFullPath(target, content);
            return;
        }

        WriteAtFullPath(Full(path), content);
    }

    private void WriteAtFullPath(string fullPath, byte[] content)
    {
        Node? existing = ResolveFull(fullPath, followFinal: false);
        if (existing is not null)
        {
            Node? final = Follow(existing);
            if (final is not null)
            {
                SetContent(final, content);
                return;
            }

            if (existing.LinkTarget is not null)
            {
                Remove(existing);
            }
        }

        string name = Path.GetFileName(fullPath);
        string? parentPath = Path.GetDirectoryName(fullPath);
        Node parent = (parentPath is null ? null : ResolveFull(parentPath, followFinal: true))
            ?? throw new DirectoryNotFoundException($"Could not find a part of the path '{fullPath}'.");
        if (!parent.IsDirectory)
        {
            throw new DirectoryNotFoundException($"Could not find a part of the path '{fullPath}'.");
        }

        Node? race = Child(parent, name);
        if (race is not null)
        {
            SetContent(Follow(race) ?? race, content);
            return;
        }

        parent.Children[name] = NewFile(name, parent, content);
    }

    private void WriteAtomic(string path, byte[] content)
    {
        string full = Full(path);
        string? parentPath = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException($"The path '{path}' has no parent directory.");
        CreateDirectoryCore(parentPath, caseSensitiveNames: null);

        Node? existing = ResolveFull(full, followFinal: false);
        if (existing is not null && existing.LinkTarget is not null)
        {
            Remove(existing);
            existing = null;
        }

        if (existing is { IsDirectory: true })
        {
            throw new UnauthorizedAccessException($"The path '{path}' is a directory, not a file.");
        }

        if (existing is not null)
        {
            SetContent(existing, content);
            return;
        }

        WriteAtFullPath(full, content);
    }

    private void SetContent(Node node, byte[] content)
    {
        if (node.IsDirectory)
        {
            throw new UnauthorizedAccessException($"The path '{CanonicalPath(node)}' is a directory, not a file.");
        }

        if ((node.Attributes & FileAttributes.ReadOnly) != 0)
        {
            throw new UnauthorizedAccessException($"The file '{CanonicalPath(node)}' is read-only.");
        }

        node.Content = content;
        node.LastWriteTimeUtc = Now;
    }

    private void Remove(Node node)
    {
        if (node.Parent is null)
        {
            throw new UnauthorizedAccessException("The in-memory root cannot be removed.");
        }

        node.Parent.Children.Remove(node.Name);
    }

    private void DeleteNodeNoFollow(Node node)
    {
        if (node.LinkTarget is not null)
        {
            Remove(node);
            return;
        }

        if (node.IsDirectory)
        {
            foreach (Node child in node.Children.Values.ToList())
            {
                DeleteNodeNoFollow(child);
            }
        }

        Remove(node);
    }

    private void CreateLink(string linkPath, string targetPath, bool namesDirectory)
    {
        ArgumentNullException.ThrowIfNull(targetPath);
        Enter(namesDirectory ? nameof(CreateDirectoryLink) : nameof(CreateFileLink), linkPath);
        string full = Full(linkPath);
        string? parentPath = Path.GetDirectoryName(full);
        if (parentPath is not null)
        {
            CreateDirectoryCore(parentPath, caseSensitiveNames: null);
        }

        Node parent = ResolveParent(linkPath, out string name);
        if (Child(parent, name) is not null)
        {
            throw new IOException($"The path '{linkPath}' already exists.");
        }

        parent.Children[name] = new Node(name, parent, namesDirectory, _names, Now)
        {
            LinkTarget = targetPath,
            LinkNamesDirectory = namesDirectory,
            Attributes = FileAttributes.ReparsePoint,
        };
    }

    private IEnumerable<string> ReadLinesCore(string path)
    {
        Node node = ResolveRequiredFile(path);
        using MemoryStream stream = new(node.Content, writable: false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private IEnumerable<string> EnumerateDirectoriesCore(string directory)
    {
        Node? resolved = Follow(Resolve(directory, followFinal: false));
        if (resolved is not { IsDirectory: true })
        {
            yield break;
        }

        string currentPath = Full(directory);
        foreach (Node child in Ordered(resolved))
        {
            if (IsDirectoryEntry(child))
            {
                yield return Path.Combine(currentPath, child.Name);
            }
        }
    }

    private IEnumerable<FileSystemEntry> EnumerateEntriesCore(string directory)
    {
        Node? resolved = Follow(Resolve(directory, followFinal: false));
        if (resolved is not { IsDirectory: true })
        {
            yield break;
        }

        string currentPath = Full(directory);
        foreach (Node child in Ordered(resolved))
        {
            yield return new FileSystemEntry(Path.Combine(currentPath, child.Name), IsDirectoryEntry(child), child.LinkTarget is not null);
        }
    }

    private IEnumerable<string> EnumerateFilesCore(string directory, string searchPattern, bool recursive, bool followLinks, HashSet<Node> visited)
    {
        Node? resolved = Follow(Resolve(directory, followFinal: false));
        if (resolved is not { IsDirectory: true } || !visited.Add(resolved))
        {
            yield break;
        }

        string currentPath = Full(directory);
        foreach (Node child in Ordered(resolved))
        {
            if (IsFileEntry(child) && Matches(searchPattern, child.Name))
            {
                yield return Path.Combine(currentPath, child.Name);
            }
        }

        if (!recursive)
        {
            yield break;
        }

        foreach (Node child in Ordered(resolved))
        {
            if (!IsDirectoryEntry(child) || SkippedDirectories.Contains(child.Name))
            {
                continue;
            }

            if (!followLinks && child.LinkTarget is not null)
            {
                continue;
            }

            foreach (string file in EnumerateFilesCore(Path.Combine(currentPath, child.Name), searchPattern, recursive: true, followLinks, visited))
            {
                yield return file;
            }
        }
    }

    private bool IsFileEntry(Node node)
    {
        if (node.LinkTarget is null)
        {
            return !node.IsDirectory;
        }

        try
        {
            return Follow(node) is { IsDirectory: false };
        }
        catch (IOException)
        {
            // A link whose chain is a cycle names no target, so the kind it was created with is the only answer.
            return !node.LinkNamesDirectory;
        }
    }

    private bool Matches(string searchPattern, string name) =>
        FileSystemName.MatchesSimpleExpression(searchPattern, name, ignoreCase: !CaseSensitive);

    /// <summary>A write stream over a file created by <see cref="OpenWriteNew"/>; its bytes land in the file when the stream is disposed.</summary>
    private sealed class NewFileStream(Node file, TimeProvider time) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                file.Content = ToArray();
                file.LastWriteTimeUtc = time.GetUtcNow().UtcDateTime;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A read-write stream over a file held exclusively; its bytes land in the file, and the hold ends, when it is disposed.</summary>
    private sealed class ExclusiveStream : MemoryStream
    {
        private readonly Node _file;
        private readonly TimeProvider _time;
        private bool _released;

        internal ExclusiveStream(Node file, TimeProvider time)
        {
            _file = file;
            _time = time;
            Write(file.Content);
            Position = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_released)
            {
                _released = true;
                _file.Content = ToArray();
                _file.LastWriteTimeUtc = _time.GetUtcNow().UtcDateTime;
                _file.ExclusiveHeld = false;
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A write-only stream that adds its bytes to the end of a file when it is disposed.</summary>
    private sealed class AppendStream(Node file, TimeProvider time) : MemoryStream
    {
        private bool _released;

        public override bool CanRead => false;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_released)
            {
                _released = true;
                file.Content = [.. file.Content, .. ToArray()];
                file.LastWriteTimeUtc = time.GetUtcNow().UtcDateTime;
                file.AppendersOpen--;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class Node
    {
        internal Node(
            string name,
            Node? parent,
            bool isDirectory,
            StringComparer names,
            DateTime lastWriteTimeUtc,
            bool caseSensitiveNames = false)
        {
            Name = name;
            Parent = parent;
            IsDirectory = isDirectory;
            Children = new Dictionary<string, Node>(
                caseSensitiveNames ? StringComparer.Ordinal : names);
            Attributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal;
            LastWriteTimeUtc = lastWriteTimeUtc;
        }

        internal string Name { get; set; }

        internal Node? Parent { get; set; }

        internal bool IsDirectory { get; }

        internal Dictionary<string, Node> Children { get; set; }

        internal byte[] Content { get; set; } = [];

        internal string? LinkTarget { get; set; }

        internal bool LinkNamesDirectory { get; set; }

        internal bool ExclusiveHeld { get; set; }

        internal int AppendersOpen { get; set; }

        internal FileAttributes Attributes { get; set; }

        internal DateTime LastWriteTimeUtc { get; set; }
    }
}
