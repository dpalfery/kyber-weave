using System.Diagnostics;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Everything the process seam needs to start one program and collect its result.</summary>
public sealed record ProcessRunRequest
{
    /// <summary>The largest amount of output a run may produce when the caller names no limit.</summary>
    /// <remarks>Matches the bound the hooks runner has always applied, so moving onto this seam keeps that behaviour.</remarks>
    public const long DefaultMaxOutputBytes = 4 * 1024 * 1024;

    /// <summary>The program to start, resolved on the path exactly as the operating system does.</summary>
    public required string Program { get; init; }

    /// <summary>One element per argument; never a command line that a shell would re-parse.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The directory to start in; null keeps the host's current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// The complete environment for the child; null inherits the host's. A key whose value is null is removed.
    /// </summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Longest the program may run before its process tree is killed; null for no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>The complete text written to standard input before it is closed; null writes nothing.</summary>
    public string? StandardInput { get; init; }

    /// <summary>The most bytes each output stream may produce; null for no limit.</summary>
    public long? MaxOutputBytes { get; init; }

    /// <summary>Describes one run of a program that is already configured as a <see cref="ProcessStartInfo"/>.</summary>
    /// <param name="startInfo">The configured program; its argument list is copied, never its argument string.</param>
    /// <param name="standardInput">The text written to standard input.</param>
    /// <param name="timeout">The longest the process may run; null for no limit.</param>
    /// <returns>A request the seam can run.</returns>
    public static ProcessRunRequest FromStartInfo(ProcessStartInfo startInfo, string standardInput, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string?> pair in startInfo.Environment)
        {
            environment[pair.Key] = pair.Value;
        }

        return new ProcessRunRequest
        {
            Program = startInfo.FileName,
            Arguments = [.. startInfo.ArgumentList],
            WorkingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
            Environment = environment,
            Timeout = timeout,
            StandardInput = standardInput,
            // The role views never name a limit, so a chatty child could otherwise buffer without bound.
            MaxOutputBytes = DefaultMaxOutputBytes
        };
    }
}
