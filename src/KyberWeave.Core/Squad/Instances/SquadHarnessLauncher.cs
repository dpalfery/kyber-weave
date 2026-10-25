using System.Buffers;
using System.Diagnostics;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     Prepares launch configuration and start information for Squad harness instances.
/// </summary>
/// <remarks>
///     Ensures isolated execution by redirecting harness homes to preset instance directories,
///     validating arguments against injection attacks, locating shims and binaries on PATH,
///     and wrapping Windows batch scripts in %ComSpec% command invocations.
/// </remarks>
public static class SquadHarnessLauncher
{
    private static readonly SearchValues<char>
        DangerousCharacters = SearchValues.Create('"', '%', '!', '^', '\r', '\n');

    /// <summary>
    ///     Builds process start information for launching a harness instance with the specified configuration.
    /// </summary>
    /// <remarks>
    ///     Validates arguments against injection attacks, locates the harness executable on PATH,
    ///     injects the instance root environment variable, and formats Windows .cmd/.bat shim invocations
    ///     via %ComSpec%.
    /// </remarks>
    /// <param name="row">The harness instance catalog row describing executable, variable, and status.</param>
    /// <param name="instanceRoot">The directory containing isolated instance configuration.</param>
    /// <param name="args">Arguments to forward to the harness executable.</param>
    /// <param name="getEnv">Environment variable accessor function.</param>
    /// <param name="findOnPath">Function to resolve executable name to its filesystem path on PATH.</param>
    /// <param name="isWindows">Indicates whether the host operating system is Windows.</param>
    /// <returns>A configured <see cref="ProcessStartInfo" /> ready to launch.</returns>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    /// <exception cref="ArgumentException">Thrown when arguments contain dangerous injection characters.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the harness executable cannot be located on PATH.</exception>
    public static ProcessStartInfo BuildStartInfo(
        SquadInstanceRow row,
        string instanceRoot,
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        Func<string, string?> findOnPath,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(instanceRoot);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnv);
        ArgumentNullException.ThrowIfNull(findOnPath);

        foreach (string arg in args)
            if (arg.AsSpan().IndexOfAny(DangerousCharacters) >= 0)
                throw new ArgumentException(
                    $"Argument contains dangerous characters and cannot be safely launched: '{arg}'",
                    nameof(args));

        string? resolvedPath = findOnPath(row.Executable);

        if (resolvedPath is null && isWindows)
        {
            string pathExt = getEnv("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
            string[] extensions =
                pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string ext in extensions)
            {
                string candidate = row.Executable.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                    ? row.Executable
                    : row.Executable + ext;
                resolvedPath = findOnPath(candidate);
                if (resolvedPath is not null) break;
            }
        }

        if (resolvedPath is null)
            throw new FileNotFoundException(
                $"Harness executable '{row.Executable}' was not found on PATH.",
                row.Executable);

        ProcessStartInfo startInfo = new()
        {
            UseShellExecute = false
        };

        if (!string.IsNullOrEmpty(row.Variable)) startInfo.Environment[row.Variable] = instanceRoot;

        bool isCmdOrBat = isWindows && (
            resolvedPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
            resolvedPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        if (isCmdOrBat)
        {
            // Task 16: Departure from ArgumentList for Windows cmd/bat shims
            string? comSpec = getEnv("ComSpec");
            startInfo.FileName = !string.IsNullOrWhiteSpace(comSpec) ? comSpec : "cmd.exe";

            List<string> parts = [$"\"{resolvedPath}\""];
            foreach (string arg in args) parts.Add($"\"{arg}\"");

            startInfo.Arguments = $"/d /s /c \"{string.Join(" ", parts)}\"";
        }
        else
        {
            startInfo.FileName = resolvedPath;
            foreach (string arg in args) startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    /// <summary>
    ///     Builds process start information for launching a harness instance using default environment and PATH resolution.
    /// </summary>
    /// <param name="row">The harness instance catalog row describing executable, variable, and status.</param>
    /// <param name="instanceRoot">The directory containing isolated instance configuration.</param>
    /// <param name="args">Arguments to forward to the harness executable.</param>
    /// <returns>A configured <see cref="ProcessStartInfo" /> ready to launch.</returns>
    public static ProcessStartInfo BuildStartInfo(
        SquadInstanceRow row,
        string instanceRoot,
        IReadOnlyList<string> args)
    {
        return BuildStartInfo(
            row,
            instanceRoot,
            args,
            Environment.GetEnvironmentVariable,
            name => FindOnPath(name),
            OperatingSystem.IsWindows());
    }

    /// <summary>
    ///     Locates an executable file by name within directories listed in the PATH environment variable.
    /// </summary>
    /// <param name="executableName">The name or relative path of the executable.</param>
    /// <param name="getEnv">Optional environment variable accessor. Defaults to system environment variables.</param>
    /// <returns>The fully qualified path to the executable, or <c>null</c> if not found.</returns>
    public static string? FindOnPath(string executableName, Func<string, string?>? getEnv = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        if (Path.IsPathRooted(executableName) && File.Exists(executableName)) return executableName;

        Func<string, string?> env = getEnv ?? Environment.GetEnvironmentVariable;
        string? pathVar = env("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;

        string[] directories = pathVar.Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string directory in directories)
            try
            {
                string candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Skip unreadable or malformed directory entries
            }

        return null;
    }
}
