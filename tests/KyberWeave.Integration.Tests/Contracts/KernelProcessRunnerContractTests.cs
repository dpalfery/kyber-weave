using System.Collections;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.Processes;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the process contract against the real adapter, which starts a real program.</summary>
public sealed class KernelProcessRunnerContractTests : ProcessRunnerContractTests
{
    private readonly string _workingDirectory = Directory.CreateTempSubdirectory("kw-process-contract-").FullName;

    /// <inheritdoc />
    protected override IProcessRunner CreateRunner() => new KernelProcessRunner();

    /// <inheritdoc />
    protected override ProcessRunRequest Probe(
        string[] arguments,
        string? standardInput = null,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        long? maxOutputBytes = null) =>
        ProcessProbeProgram.Request(
            arguments,
            workingDirectory ?? WorkingDirectory,
            standardInput,
            timeout,
            MarkerEnvironment(),
            maxOutputBytes);

    /// <inheritdoc />
    protected override string WorkingDirectory => _workingDirectory;

    /// <inheritdoc />
    protected override string MissingDirectory => Path.Combine(_workingDirectory, "missing");

    /// <inheritdoc />
    protected override int LiveProcessId => Environment.ProcessId;

    /// <inheritdoc />
    protected override void PrepareWorkingDirectoryFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_workingDirectory, name), content);

    /// <inheritdoc />
    public override void Dispose()
    {
        try
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // The suite never removed it; nothing is left to clean.
        }

        base.Dispose();
    }

    /// <summary>The host's environment plus the marker the contract checks.</summary>
    private static Dictionary<string, string?> MarkerEnvironment()
    {
        Dictionary<string, string?> environment = new(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value as string;
        }

        environment[MarkerVariable] = MarkerValue;
        return environment;
    }
}
