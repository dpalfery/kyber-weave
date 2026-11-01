using Xunit;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests;

/// <summary>
/// Shared arrangement for the doctor tests. Every child is either a scripted <see cref="IProcessExecutor"/> answer
/// or a trivial shell stub written into a temp directory. No test spawns a real <c>codegraph</c> or the real
/// <c>kyber-weave-mcp</c>.
/// </summary>
internal static class DoctorHarness
{
    /// <summary>A missing binary, as a process start reports it when the file is not on PATH.</summary>
    public static Exception BinaryNotFound() => new System.ComponentModel.Win32Exception(2);

    /// <summary>
    /// Answers <c>git rev-parse --show-toplevel</c> with <paramref name="root"/> and every <c>codegraph</c> call
    /// with <paramref name="codegraph"/>. Any other child is a test error, so an unexpected spawn fails loudly.
    /// </summary>
    public static ScriptedProcessExecutor Executor(
        string root,
        Func<IReadOnlyList<string>, ProcessResult>? codegraph = null) =>
        new(startInfo =>
        {
            if (startInfo.FileName == "git")
                return new ProcessResult(0, root + "\n", string.Empty);
            if (startInfo.FileName == "codegraph")
            {
                IReadOnlyList<string> arguments = startInfo.ArgumentList.ToArray();
                return codegraph?.Invoke(arguments) ?? Ok();
            }

            throw new InvalidOperationException($"Unexpected child process in a doctor test: '{startInfo.FileName}'.");
        });

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, string.Empty);

    /// <summary>Options with the given MCP executable and budget; the index timeout is the contract default.</summary>
    public static DoctorOptions Options(string mcpExecutable, TimeSpan? mcpTimeout = null) =>
        new(TimeSpan.FromSeconds(600), mcpTimeout ?? TimeSpan.FromSeconds(20), "codegraph", mcpExecutable);

    /// <summary>
    /// Writes an executable <c>/bin/sh</c> MCP stub into <paramref name="directory"/> and returns its path. The
    /// stub is a fixed script of canned answers, not the real server.
    /// </summary>
    public static string WriteStub(string directory, string body)
    {
        string path = Path.Combine(directory, "kyber-weave-mcp-stub");
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>A healthy server: answers <c>initialize</c> and <c>tools/call</c> for <paramref name="root"/>.</summary>
    public static string HealthyStub(string directory, string root) =>
        WriteStub(directory, """
            read _initialize
            printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"kyber-weave-mcp","version":"0.0.0"}}}'
            read _initialized
            read _call
            printf '%s\n' '{"jsonrpc":"2.0","id":2,"result":{"isError":false,"content":[{"type":"text","text":"provenance: root=ROOT"}]}}'
            cat > /dev/null
            """.Replace("ROOT", root, StringComparison.Ordinal));

    /// <summary>Behaves like a healthy server and records the argv it was started with, one argument per line.</summary>
    public static string RecordingStub(string directory, string root, string argvPath) =>
        WriteStub(directory, """
            printf '%s\n' "$@" > "ARGV"
            read _initialize
            printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"kyber-weave-mcp","version":"0.0.0"}}}'
            read _initialized
            read _call
            printf '%s\n' '{"jsonrpc":"2.0","id":2,"result":{"isError":false,"content":[{"type":"text","text":"provenance: root=ROOT"}]}}'
            cat > /dev/null
            """.Replace("ARGV", argvPath, StringComparison.Ordinal).Replace("ROOT", root, StringComparison.Ordinal));

    /// <summary>Answers the handshake, then reports <c>docs_explore</c> as an error result.</summary>
    public static string ErroringDocsExploreStub(string directory, string root) =>
        WriteStub(directory, """
            read _initialize
            printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"kyber-weave-mcp","version":"0.0.0"}}}'
            read _initialized
            read _call
            printf '%s\n' '{"jsonrpc":"2.0","id":2,"result":{"isError":true,"content":[{"type":"text","text":"index not loaded for ROOT"}]}}'
            cat > /dev/null
            """.Replace("ROOT", root, StringComparison.Ordinal));

    /// <summary>Answers everything, but from a different checkout than the root.</summary>
    public static string WrongRootStub(string directory) =>
        WriteStub(directory, """
            read _initialize
            printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"kyber-weave-mcp","version":"0.0.0"}}}'
            read _initialized
            read _call
            printf '%s\n' '{"jsonrpc":"2.0","id":2,"result":{"isError":false,"content":[{"type":"text","text":"provenance: root=/elsewhere/checkout"}]}}'
            cat > /dev/null
            """);

    /// <summary>Starts, then never answers: the handshake waits until its budget expires.</summary>
    public static string SilentStub(string directory) =>
        WriteStub(directory, "sleep 30");

    public static DoctorCheck CheckNamed(DoctorReport report, string id) =>
        Assert.Single(report.Checks, check => check.Id == id);

    public static void AssertNoCall(ScriptedProcessExecutor executor, string verb) =>
        Assert.DoesNotContain(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains(verb, StringComparer.Ordinal));
}
