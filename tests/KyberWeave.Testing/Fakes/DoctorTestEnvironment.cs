using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests;

/// <summary>A process environment for the doctor command: a chosen terminal state and a chosen environment reader.</summary>
internal static class DoctorTestEnvironment
{
    /// <summary>Creates the environment, over the running process's directories.</summary>
    /// <param name="terminal">Whether standard input is a terminal.</param>
    /// <param name="read">Answers each environment variable.</param>
    /// <returns>The environment.</returns>
    internal static IProcessEnvironment Create(bool terminal, Func<string, string?> read) =>
        new FakeProcessEnvironment(currentDirectory: Environment.CurrentDirectory, isInputRedirected: !terminal, readVariable: read);

    /// <summary>The <c>doctor</c> command over the real adapters and the environment this builds.</summary>
    /// <param name="terminal">Whether standard input is a terminal.</param>
    /// <param name="read">Answers each environment variable.</param>
    /// <param name="executor">Runs the CodeGraph child.</param>
    /// <param name="mcpExecutable">The MCP executable to probe.</param>
    internal static KyberWeave.Cli.Commands.Doctor.DoctorCommand Command(
        bool terminal,
        Func<string, string?> read,
        KyberWeave.Contracts.Repositories.IProcessExecutor executor,
        string mcpExecutable) =>
        DoctorTestServices.Command(Create(terminal, read), executor, mcpExecutable);
}
