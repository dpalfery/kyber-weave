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
        FakeProcessEnvironment.UseRunningProcess(read, isInputRedirected: !terminal);
}
