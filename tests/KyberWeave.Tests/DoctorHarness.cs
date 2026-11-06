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
/// <remarks>
/// The MCP stubs assert the tool name the probe requests. Each stub answers <c>tools/call</c> with its scripted
/// reply only when the request names <see cref="ProbedTool"/>; any other name gets a reply the probe must not
/// accept. A probe still asking for the retired name therefore fails its check instead of passing against a stub
/// that ignored the name.
/// </remarks>
internal static class DoctorHarness
{
    /// <summary>The docs tool the live probe must request. The stubs answer only this name.</summary>
    public const string ProbedTool = "docs_answer_question_from_repo_documentation";

    /// <summary>The reply a healthy server gives <c>initialize</c>.</summary>
    public const string InitializeReply = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"kyber-weave-mcp","version":"0.0.0"}}}""";

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

    /// <summary>A healthy server: answers <c>initialize</c> and the probed tool for <paramref name="root"/>.</summary>
    public static string HealthyStub(string directory, string root) =>
        WriteStub(directory, Preamble() + "read _call\n" + CallBranch(Reply(false, Provenance(root)), UnknownTool()));

    /// <summary>Behaves like a healthy server and records the argv it was started with, one argument per line.</summary>
    public static string RecordingStub(string directory, string root, string argvPath) =>
        WriteStub(
            directory,
            $"printf '%s\\n' \"$@\" > \"{argvPath}\"\n"
            + Preamble()
            + "read _call\n"
            + CallBranch(Reply(false, Provenance(root)), UnknownTool()));

    /// <summary>
    /// Behaves like a healthy server and records the <c>tools/call</c> request line it received, so a test can read
    /// the tool name the probe asked for.
    /// </summary>
    public static string CallRecordingStub(string directory, string root, string callPath) =>
        WriteStub(
            directory,
            Preamble()
            + "read _call\n"
            + $"printf '%s\\n' \"$_call\" > \"{callPath}\"\n"
            + CallBranch(Reply(false, Provenance(root)), UnknownTool()));

    /// <summary>
    /// Answers the handshake, then reports the probed docs tool as an error result. Any other tool name gets a
    /// healthy provenance reply, so a probe that still asks for the retired name would pass and the test would fail.
    /// </summary>
    public static string ErroringDocsExploreStub(string directory, string root) =>
        WriteStub(
            directory,
            Preamble()
            + "read _call\n"
            + CallBranch(Reply(true, "index not loaded for " + root), Reply(false, Provenance(root))));

    /// <summary>Answers everything, but from a different checkout than the root.</summary>
    public static string WrongRootStub(string directory) =>
        WriteStub(
            directory,
            Preamble()
            + "read _call\n"
            + CallBranch(Reply(false, "provenance: root=/elsewhere/checkout"), UnknownTool()));

    /// <summary>Starts, then never answers: the handshake waits until its budget expires.</summary>
    public static string SilentStub(string directory) =>
        WriteStub(directory, "sleep 30");

    /// <summary>
    /// Answers <c>initialize</c> with <paramref name="initializeReply"/> and the probed tool with
    /// <paramref name="callReply"/>, so a test can put arbitrary JSON in front of the probe's parser.
    /// </summary>
    public static string ReplyingStub(string directory, string initializeReply, string callReply) =>
        WriteStub(
            directory,
            "read _initialize\n"
            + $"printf '%s\\n' '{initializeReply}'\n"
            + "read _initialized\n"
            + "read _call\n"
            + CallBranch(callReply, UnknownTool()));

    /// <summary>
    /// Answers <c>initialize</c>, then closes its own standard input. The close happens before the reply is
    /// written, so the probe's next write is guaranteed to see a broken pipe rather than racing the child.
    /// </summary>
    public static string ClosesInputAfterInitializeStub(string directory) =>
        WriteStub(directory, """
            exec 0<&-
            printf '%s\n' '{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"kyber-weave-mcp","version":"0.0.0"}}}'
            sleep 30
            """);

    public static DoctorCheck CheckNamed(DoctorReport report, string id) =>
        Assert.Single(report.Checks, check => check.Id == id);

    public static void AssertNoCall(ScriptedProcessExecutor executor, string verb) =>
        Assert.DoesNotContain(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains(verb, StringComparer.Ordinal));

    /// <summary>The shell lines that answer <c>initialize</c> and consume <c>notifications/initialized</c>.</summary>
    private static string Preamble() =>
        "read _initialize\n"
        + $"printf '%s\\n' '{InitializeReply}'\n"
        + "read _initialized\n";

    /// <summary>
    /// The <c>tools/call</c> branch. The request line has already been read into <c>_call</c>. The probed tool gets
    /// <paramref name="probedReply"/>; a request naming any other tool gets <paramref name="otherReply"/>.
    /// </summary>
    private static string CallBranch(string probedReply, string otherReply) =>
        "case \"$_call\" in\n"
        + $"  *'\"name\":\"{ProbedTool}\"'*) printf '%s\\n' '{probedReply}' ;;\n"
        + $"  *) printf '%s\\n' '{otherReply}' ;;\n"
        + "esac\n"
        + "cat > /dev/null\n";

    /// <summary>A JSON-RPC tool result carrying one text item.</summary>
    private static string Reply(bool isError, string text) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":" + (isError ? "true" : "false")
        + ",\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}]}}";

    private static string Provenance(string root) => "provenance: root=" + root;

    private static string UnknownTool() => Reply(true, "unknown tool");
}
