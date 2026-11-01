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
/// </remarks>
public sealed class LaunchCommandResolver
{
    /// <summary>What Windows searches when a command names no extension.</summary>
    private const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    private readonly IReadOnlyList<string> _searchPaths;

    /// <summary>Creates a resolver over an ordered list of directories to search.</summary>
    /// <param name="searchPaths">
    /// Directories in <c>PATH</c> order: the first one holding the command is the one a launch
    /// would use, so a later entry never masks the earlier answer.
    /// </param>
    public LaunchCommandResolver(IEnumerable<string> searchPaths)
    {
        ArgumentNullException.ThrowIfNull(searchPaths);

        _searchPaths =
        [
            .. searchPaths.Where(path => !string.IsNullOrWhiteSpace(path))
        ];
    }

    /// <summary>
    /// A resolver over this process's <c>PATH</c> — the composition-root entry point, for a caller
    /// that wants the real machine rather than an injected search path.
    /// </summary>
    public static LaunchCommandResolver FromEnvironment() =>
        new(SplitSearchPath(Environment.GetEnvironmentVariable("PATH")));

    /// <summary>
    /// The full path a launch of <paramref name="command"/> would use, or null when no search
    /// path holds it.
    /// </summary>
    /// <param name="command">
    /// A command name, or a path. A name is looked up in every search path in order; a path is
    /// resolved as given, because a configured path is not something <c>PATH</c> can override.
    /// </param>
    public string? Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        if (IsPath(command))
            return FindNear(command, directory: null);

        foreach (string searchPath in _searchPaths)
        {
            string? resolved = FindNear(command, searchPath);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private static bool IsPath(string command) =>
        Path.IsPathRooted(command) ||
        command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
        command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static string? FindNear(string command, string? directory)
    {
        foreach (string candidate in Candidates(command, directory))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// The file names one command can be stored under: itself, then — on Windows — itself with
    /// each <c>PATHEXT</c> extension it does not already carry. Mirrors the launcher's rule, since
    /// the check would otherwise report a working Windows install as unlaunchable.
    /// </summary>
    private static IEnumerable<string> Candidates(string command, string? directory)
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

    private static IEnumerable<string> PathExtensions()
    {
        string configured = Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultPathExtensions;

        return configured.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<string> SplitSearchPath(string? searchPath) =>
        string.IsNullOrEmpty(searchPath)
            ? []
            : searchPath.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
