using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Core.Docs.Mcp;

/// <summary>
/// Resolves a launch command name to a file on the search path, so the wiring check can say
/// whether a harness's configured entry would actually start.
/// </summary>
/// <remarks>
/// <para>
/// This is a file-existence check and nothing more: no process is started, no <c>--version</c> is
/// probed, and the docs branch takes no dependency on Squad's process probes. A wiring check
/// needs to know that the binary is reachable where the harness will look — which is what
/// <c>PATH</c> is — not that it answers.
/// </para>
/// <para>
/// The search paths are injected rather than read from the environment so that the check is a
/// pure function of the repository tree and the paths it was handed: a test never resolves
/// against the developer's own <c>PATH</c>, and the same tree yields the same report on every
/// machine.
/// </para>
/// <para>
/// The repository root is injected for the same reason, and because it is the answer to a second
/// question the search path cannot answer. A declared command that names a path —
/// <c>./tools/serve-mcp</c>, <c>scripts/serve.sh</c> — is not something <c>PATH</c> resolves, so
/// it used to be resolved against whatever directory the process happened to start in: the same
/// repository was launchable from one shell and reported <c>KW-MCP-WIRE-003</c> from another, and
/// the check and the launch that followed it could disagree about the same entry. The file a
/// harness starts is relative to the repository root, so that is what a relative command is
/// resolved against.
/// </para>
/// </remarks>
public sealed class LaunchCommandResolver : ILaunchCommandResolver
{
    /// <summary>What Windows searches when a command names no extension.</summary>
    private const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    private readonly string _baseDirectory;
    private readonly IReadOnlyList<string> _searchPaths;
    private readonly IFileSystem _files;
    private readonly IPathResolver _paths;
    private readonly string _pathExtensions;

    /// <summary>Creates a resolver over one repository's search paths.</summary>
    /// <param name="repositoryRoot">
    /// The directory a relative command is resolved against, and the directory an empty search-path
    /// entry means.
    /// </param>
    /// <param name="searchPaths">
    /// Directories in <c>PATH</c> order: the first one holding the command is the one a launch
    /// would use, so a later entry never masks the earlier answer.
    /// </param>
    /// <param name="files">The file system a candidate's existence is checked on.</param>
    /// <param name="paths">The path seam a relative root or command is resolved through.</param>
    /// <param name="environment">The process facts: the working directory a relative root means, and <c>PATHEXT</c>.</param>
    public LaunchCommandResolver(
        string repositoryRoot,
        IEnumerable<string> searchPaths,
        IFileSystem files,
        IPathResolver paths,
        IProcessEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(searchPaths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(environment);

        _files = files;
        _paths = paths;
        _pathExtensions = environment.GetVariable("PATHEXT") ?? DefaultPathExtensions;
        _baseDirectory = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        _searchPaths = [.. searchPaths];
    }

    /// <summary>
    /// The full path a launch of <paramref name="command"/> would use, or null when it names no file
    /// this resolver can find.
    /// </summary>
    /// <param name="command">
    /// A command name, or a path. A name is looked up in every search path in order; a path is taken
    /// as given when it is fully qualified and resolved against the repository root when it is not,
    /// because a configured path is not something <c>PATH</c> can override.
    /// </param>
    public string? Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (IsPath(command))
        {
            string? absolute = Absolute(command);
            return absolute is null ? null : FindNear(absolute, directory: null);
        }

        foreach (string searchPath in _searchPaths)
        {
            string? resolved = FindNear(command, searchPath.Length == 0 ? _baseDirectory : searchPath);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private static bool IsPath(string command) =>
        Path.IsPathRooted(command) ||
        IsDriveRelative(command) ||
        command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// The Windows form <c>C:serve-mcp</c>, which names a path on a drive rather than a command to
    /// look up. It is not fully qualified — it is relative to that drive's own working directory —
    /// so it is resolved like any other relative path instead of being searched for as a name.
    /// </summary>
    private static bool IsDriveRelative(string command) =>
        OperatingSystem.IsWindows() &&
        !Path.IsPathFullyQualified(command) &&
        command.Length >= 2 &&
        char.IsAsciiLetter(command[0]) &&
        command[1] == ':';

    /// <summary>
    /// The command as a fully qualified path, resolving a relative one against the repository root.
    /// Null when the string cannot name a file at all — a path the platform rejects is not
    /// launchable, and that answer belongs to the caller as a finding rather than as an exception.
    /// </summary>
    /// <remarks>
    /// The three exceptions are the complete set <see cref="Path.GetFullPath(string, string)"/>
    /// documents for a path this code composed, and they are the set
    /// <c>McpRepositoryPath.Contained</c> already answers with a refusal. Catching two of the
    /// three made the same question — can this process name that path — answer a gap in one place
    /// and a crash in the other: a Windows-shaped colon outside the volume position (a command
    /// spelled as a URL is how one arrives) threw past this method and out of the check, so a
    /// declaration nobody could launch took the run down instead of being reported at
    /// <c>KW-MCP-WIRE-003</c>.
    /// </remarks>
    private string? Absolute(string command)
    {
        if (Path.IsPathFullyQualified(command))
            return command;

        try
        {
            return _paths.GetFullPath(command, _baseDirectory);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private string? FindNear(string command, string? directory)
    {
        foreach (string candidate in Candidates(command, directory))
        {
            if (_files.FileExists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The file names one command can be stored under: itself, then — on Windows — itself with
    /// each <c>PATHEXT</c> extension it does not already carry. Mirrors the launcher's rule, since
    /// the check would otherwise report a working Windows install as unlaunchable.
    /// </summary>
    private IEnumerable<string> Candidates(string command, string? directory)
    {
        yield return Combine(directory, command);

        if (!OperatingSystem.IsWindows())
            yield break;

        foreach (string extension in PathExtensions())
        {
            if (command.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;

            yield return Combine(directory, command + extension);
        }
    }

    private static string Combine(string? directory, string name) =>
        string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);

    private IEnumerable<string> PathExtensions() =>
        _pathExtensions.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Builds the launch-command resolver over the process's <c>PATH</c>, read through the environment seam.</summary>
/// <param name="files">The file system a candidate's existence is checked on.</param>
/// <param name="paths">The path seam a relative root or command is resolved through.</param>
/// <param name="environment">The process facts the resolver reads.</param>
public sealed class EnvironmentLaunchCommandResolverFactory(IFileSystem files, IPathResolver paths, IProcessEnvironment environment)
    : ILaunchCommandResolverFactory
{
    /// <inheritdoc />
    public ILaunchCommandResolver FromEnvironment(string repositoryRoot) =>
        new LaunchCommandResolver(repositoryRoot, SplitSearchPath(environment.GetVariable("PATH")), files, paths, environment);

    /// <summary>A resolver over the process's <c>PATH</c> that resolves a relative command against the working directory.</summary>
    /// <returns>The resolver.</returns>
    public ILaunchCommandResolver FromEnvironment() => FromEnvironment(environment.CurrentDirectory);

    /// <summary>
    /// Splits a <c>PATH</c> value into its entries, keeping every one of them — including an empty
    /// one, which is a directory meaning and not a missing value.
    /// </summary>
    private static IEnumerable<string> SplitSearchPath(string? searchPath) =>
        searchPath is null ? [] : searchPath.Split(Path.PathSeparator);
}
