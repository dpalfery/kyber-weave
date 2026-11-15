using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using KyberDash.Core.Processes;

namespace KyberDash.Cli;

/// <summary>
/// One sibling-executable launch: the executable, the arguments after its own verb, and whether it
/// needs the caller's standard input.
/// </summary>
public class ProcessInvocation
{
    /// <summary>Creates one launch description.</summary>
    /// <param name="fileName">Absolute path, or a bare name resolved from <c>PATH</c>.</param>
    /// <param name="arguments">Arguments passed after the verb.</param>
    /// <param name="forwardStandardInput">Whether the child consumes the caller's standard input (<c>mcp</c> does).</param>
    public ProcessInvocation(string fileName, IReadOnlyList<string> arguments, bool forwardStandardInput)
    {
        FileName = fileName;
        Arguments = arguments;
        ForwardStandardInput = forwardStandardInput;
    }

    /// <summary>Absolute path, or a bare name resolved from <c>PATH</c>.</summary>
    public string FileName { get; }

    /// <summary>Arguments passed after the verb.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Whether the child consumes the caller's standard input.</summary>
    public bool ForwardStandardInput { get; }

    /// <summary>Renders the command line for a diagnostic, quoting arguments that contain spaces.</summary>
    /// <returns>A shell-like rendering of the command.</returns>
    public string Describe()
    {
        List<string> parts = [FileName];
        foreach (string argument in Arguments)
        {
            bool needsQuotes = argument.Contains(' ', StringComparison.Ordinal)
                || argument.Contains('\t', StringComparison.Ordinal)
                || argument.Contains('"', StringComparison.Ordinal);

            parts.Add(needsQuotes
                ? "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : argument);
        }

        return string.Join(' ', parts);
    }
}

/// <summary>Starts the separate <c>web</c>, <c>mcp</c>, and <c>otel</c> executables.</summary>
/// <remarks>Injected so a test can assert what the router asked for without starting a process.</remarks>
public interface IProcessMode
{
    /// <summary>Runs <paramref name="invocation"/> to completion, forwarding its output.</summary>
    /// <param name="invocation">The executable and arguments.</param>
    /// <param name="output">Receives the child's standard output.</param>
    /// <param name="standardError">Receives the child's standard error.</param>
    /// <param name="cancellationToken">Stops the child.</param>
    /// <returns>The child's exit code, or <c>1</c> when it could not be started.</returns>
    Task<int> RunAsync(ProcessInvocation invocation, TextWriter output, TextWriter standardError, CancellationToken cancellationToken);
}

/// <summary>Starts the sibling executable that ships next to <c>kyberdash</c>.</summary>
public class SiblingProcessMode : IProcessMode
{
    /// <summary>Upper bound on a child launch, so a wedged sibling cannot hang the CLI forever.</summary>
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromHours(12);

    /// <summary>Total output the parent will buffer from a child before giving up.</summary>
    public const long MaxBufferedBytes = 256L * 1024 * 1024;

    /// <summary>Resolves <paramref name="name"/> beside <see cref="Environment.ProcessPath"/> when present.</summary>
    /// <param name="name">The bare executable name, such as <c>kyberdash-api</c>.</param>
    /// <returns>The absolute sibling path when it exists, otherwise the bare name for path lookup.</returns>
    public static string SiblingExecutable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string?[] searchDirectories = [Path.GetDirectoryName(Environment.ProcessPath), AppContext.BaseDirectory];
        foreach (string? baseDirectory in searchDirectories)
        {
            if (string.IsNullOrEmpty(baseDirectory))
                continue;

            string sameDirectoryCandidate = Path.Combine(baseDirectory, fileName);
            if (File.Exists(sameDirectoryCandidate))
                return sameDirectoryCandidate;

            string? devSibling = TryResolveDevelopmentSibling(baseDirectory, name, fileName);
            if (devSibling is not null)
                return devSibling;
        }

        return fileName;
    }

    private static string? TryResolveDevelopmentSibling(string baseDirectory, string bareName, string fileName)
    {
        string? projectDirectory = bareName switch
        {
            "kyberdash-api" => "KyberDash.Api",
            "kyberdash-mcp" => "KyberDash.Mcp",
            _ => null,
        };
        if (projectDirectory is null)
            return null;

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        string? tfm = Path.GetFileName(normalized);
        string? binConfigDir = Path.GetDirectoryName(normalized);
        string? config = binConfigDir is null ? null : Path.GetFileName(binConfigDir);
        string? binDir = binConfigDir is null ? null : Path.GetDirectoryName(binConfigDir);
        string? projectDir = binDir is null ? null : Path.GetDirectoryName(binDir);
        string? serverDir = projectDir is null ? null : Path.GetDirectoryName(projectDir);
        if (serverDir is not null && !string.IsNullOrEmpty(config) && !string.IsNullOrEmpty(tfm))
        {
            string sibling = Path.Combine(serverDir, projectDirectory, "bin", config, tfm, fileName);
            if (File.Exists(sibling))
                return sibling;
        }

        string? cursor = normalized;
        for (int depth = 0; depth < 8 && cursor is not null; depth++)
        {
            if (File.Exists(Path.Combine(cursor, "KyberWeave.sln")))
            {
                foreach (string configuration in new[] { "Release", "Debug" })
                {
                    string candidate = Path.Combine(cursor, "dash", "server", projectDirectory, "bin", configuration, "net10.0", fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }

                break;
            }

            cursor = Path.GetDirectoryName(cursor);
        }

        return null;
    }

    /// <summary>Runs one invocation with streaming standard output/error and signal-aware cleanup, inheriting this process's environment.</summary>
    /// <param name="invocation">The executable and arguments.</param>
    /// <param name="output">Receives the child's standard output.</param>
    /// <param name="standardError">Receives the child's standard error.</param>
    /// <param name="cancellationToken">Stops the child.</param>
    /// <returns>The child's exit code, or <c>1</c> when it could not be started.</returns>
    public async Task<int> RunAsync(ProcessInvocation invocation, TextWriter output, TextWriter standardError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(standardError);

        ProcessStartInfo startInfo = new(invocation.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = !invocation.ForwardStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // ProcessRunner clears the environment before starting; a sibling needs ours to find the
        // operator's state directory and store.
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                startInfo.Environment[key] = value;
        }

        foreach (string argument in invocation.Arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            return await ProcessRunner.RunInteractiveAsync(
                startInfo,
                output,
                standardError,
                invocation.ForwardStandardInput,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            await standardError.WriteLineAsync(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"kyberdash: could not run {invocation.Describe()}: {ex.Message}")).ConfigureAwait(false);
            return 1;
        }
    }
}

/// <summary>Argument checks shared by the router and its tests.</summary>
public static class DashArguments
{
    private static readonly string[] ReservedVerbs = ["web", "mcp", "otel"];

    /// <summary>
    /// Whether <paramref name="args"/> are safe to append after a child's own verb: no second verb
    /// that the child would misinterpret as its mode selector.
    /// </summary>
    /// <param name="args">The arguments the caller wrote after the CLI verb.</param>
    /// <returns>Whether every argument is a plain value or an option.</returns>
    public static bool AreForwardable(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (string arg in args)
        {
            if (ReservedVerbs.Contains(arg, StringComparer.Ordinal))
                return false;
        }

        return true;
    }
}
