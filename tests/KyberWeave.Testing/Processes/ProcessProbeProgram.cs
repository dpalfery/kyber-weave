using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Processes;

/// <summary>The tiny program the process contract runs for real, and the request that starts it.</summary>
public static class ProcessProbeProgram
{
    /// <summary>The fictional program name the scripted fake answers to.</summary>
    public const string FakeProgram = "kw-process-probe";

    /// <summary>Writes one argument to standard output and exits zero.</summary>
    public const string Echo = "echo";

    /// <summary>Writes to both output streams and exits zero.</summary>
    public const string Both = "both";

    /// <summary>Writes to standard error and exits with the code in the next argument.</summary>
    public const string Fail = "fail";

    /// <summary>Copies standard input to standard output.</summary>
    public const string Cat = "cat";

    /// <summary>Reads a file relative to the working directory and writes it to standard output.</summary>
    public const string Read = "read";

    /// <summary>Writes an environment variable's value, or <c>&lt;unset&gt;</c>.</summary>
    public const string Environment = "env";

    /// <summary>Sleeps for the number of milliseconds in the next argument.</summary>
    public const string Sleep = "sleep";

    /// <summary>The probe assembly, which sits beside every test assembly that references the testing library.</summary>
    public static string AssemblyPath { get; } = Path.Combine(AppContext.BaseDirectory, "KyberWeave.Testing.ProcessProbe.dll");

    /// <summary>Builds a request that starts the probe on the dotnet host, which is present wherever these tests run.</summary>
    /// <param name="arguments">The probe instruction and its arguments.</param>
    /// <param name="workingDirectory">The directory the probe starts in.</param>
    /// <param name="standardInput">The text written to the probe, if any.</param>
    /// <param name="timeout">The longest the probe may run, if bounded.</param>
    /// <param name="environment">The complete environment for the probe.</param>
    /// <param name="maxOutputBytes">The most bytes each stream may produce, if bounded.</param>
    /// <returns>A request the seam can run.</returns>
    public static ProcessRunRequest Request(
        string[] arguments,
        string workingDirectory,
        string? standardInput = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        long? maxOutputBytes = null) =>
        new()
        {
            Program = "dotnet",
            Arguments = [AssemblyPath, .. arguments],
            WorkingDirectory = workingDirectory,
            StandardInput = standardInput,
            Timeout = timeout,
            Environment = environment,
            MaxOutputBytes = maxOutputBytes
        };
}
