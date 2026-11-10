using Xunit;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the live MCP proof (D1, D7, D8, D9) through <see cref="RepositoryDoctor.Run"/>. CodeGraph is answered by
/// the scripted executor and the server is a trivial shell stub, so the proof runs over a real stdio session with
/// no real <c>kyber-weave-mcp</c>. The stubs live in a temp directory and are removed with it.
/// </summary>
/// <remarks>
/// Test names use <c>McpHandshake</c> and <c>McpRoot</c> where the plan writes <c>mcp.handshake</c> and
/// <c>mcp.root</c>: a dot is not legal in a C# method name. The check ids asserted are the dotted ones.
/// </remarks>
public sealed class McpLiveProbeTests : IDisposable
{
    private const string Secret = "kw-doctor-test-secret-7f3a";

    private readonly TempDirectory _root = new();
    private readonly TempDirectory _stubs = new();

    public void Dispose()
    {
        _stubs.Dispose();
        _root.Dispose();
    }

    private string Root => _root.Path;

    /// <summary>
    /// Control: a server that answers for this checkout passes every MCP check. Without this the failure tests below
    /// could pass against a stub that never works.
    /// </summary>
    [Fact]
    public void McpLiveProbe_MatchingRoot_PassesAllMcpChecks()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.True(DoctorHarness.CheckNamed(report, "mcp.start").Passed);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.handshake").Passed);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.docs_explore").Passed);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// The probe asks the server for the renamed docs tool, <c>docs_answer_question_from_repo_documentation</c>, and
    /// still reports the stable check id <c>mcp.docs_explore</c>. The recorded request is the proof: it names the new
    /// tool and never the retired one, so there is no fallback to the old name.
    /// </summary>
    /// <remarks>
    /// The check id is a contract that other tooling matches on, so the rename moves the request and not the id.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_RequestsTheRenamedDocsTool_AndKeepsItsCheckId()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        string callPath = Path.Combine(_stubs.Path, "call.json");

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.CallRecordingStub(_stubs.Path, Root, callPath)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.True(DoctorHarness.CheckNamed(report, "mcp.docs_explore").Passed);
        Assert.Equal(0, report.ExitCode);
        string request = File.ReadAllText(callPath);
        Assert.Contains("\"name\":\"docs_answer_question_from_repo_documentation\"", request, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\":\"docs_explore\"", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// The probe starts the server for the repository it resolved, by passing <c>--repo-root</c>. Without the flag the
    /// child inherits the doctor's working directory, so a healthy target can fail <c>mcp.root</c> or pass against the
    /// wrong checkout — the exact confusion the probe exists to prevent.
    /// </summary>
    [Fact]
    public void McpLiveProbe_StartsTheServerWithTheResolvedRoot()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        string argvPath = Path.Combine(_stubs.Path, "argv.txt");

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.RecordingStub(_stubs.Path, Root, argvPath)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(0, report.ExitCode);
        string[] argv = File.ReadAllLines(argvPath);
        Assert.Contains("--repo-root", argv);
        Assert.Contains(Root, argv);
    }

    /// <summary>
    /// A server that starts but never answers the handshake fails <c>mcp.handshake</c> with exit 1, and the budget ends
    /// the wait. The child is killed on that path; the stub's <c>sleep</c> would otherwise outlive the run.
    /// </summary>
    /// <remarks>
    /// D7: the session has a total budget and kills the tree in <c>finally</c>. Observing the kill from outside the
    /// session needs a process seam the contract does not expose, so the assertion here is the outcome.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_HandshakeTimeout_KillsChild_Exits1_McpHandshake()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.SilentStub(_stubs.Path), TimeSpan.FromSeconds(1)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        DoctorCheck handshake = DoctorHarness.CheckNamed(report, "mcp.handshake");
        Assert.False(handshake.Passed);
        Assert.False(string.IsNullOrWhiteSpace(handshake.Fix));
    }

    /// <summary>
    /// A folder with no host configuration makes the server refuse to start. The doctor names the missing file and the
    /// command that adds it: raising the timeout, which it used to suggest, can never help a server that refused.
    /// </summary>
    [Fact]
    public void McpLiveProbe_NoHostConfiguration_NamesTheMissingFileAndTheCommandThatAddsIt()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        string stub = DoctorHarness.WriteStub(
            _stubs.Path,
            "echo 'KW-MCP-ROOT-001: No Kyber-Weave host configuration was found for MCP root' >&2\nexit 1");

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(stub),
            DoctorHarness.Executor(Root),
            _ => null);

        DoctorCheck handshake = DoctorHarness.CheckNamed(report, "mcp.handshake");
        Assert.False(handshake.Passed);
        Assert.Contains(".kyber-weave/kyber-weave.yml", handshake.Reason, StringComparison.Ordinal);
        Assert.Contains("kyber-weave docs init", handshake.Fix, StringComparison.Ordinal);
        Assert.Contains(Root, handshake.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain("--mcp-timeout", handshake.Fix, StringComparison.Ordinal);
    }

    /// <summary>
    /// A server that exits before answering did not run out of time, so the remedy is to read its own error. Only a
    /// server that really used up the budget is told to raise it.
    /// </summary>
    [Fact]
    public void McpLiveProbe_ServerThatExitsBeforeAnswering_IsNotToldToRaiseTheTimeout()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        string stub = DoctorHarness.WriteStub(_stubs.Path, "echo 'something else broke' >&2\nexit 1");

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(stub),
            DoctorHarness.Executor(Root),
            _ => null);

        DoctorCheck handshake = DoctorHarness.CheckNamed(report, "mcp.handshake");
        Assert.False(handshake.Passed);
        Assert.Contains("something else broke", handshake.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("--mcp-timeout", handshake.Fix, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-mcp", handshake.Fix, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>.codegraph/</c> folder that was never initialised, such as one holding only a committed <c>.gitignore</c>,
    /// is set up with <c>codegraph init</c> rather than failing the run.
    /// </summary>
    [Theory]
    [InlineData(DoctorMode.Ensure)]
    [InlineData(DoctorMode.Provision)]
    public void FolderNeverInitialised_IsSetUpWithInit(DoctorMode mode)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("sync", StringComparer.Ordinal)
                ? new ProcessResult(1, string.Empty, "✗ CodeGraph not initialized in " + Root)
                : DoctorHarness.Ok("1.5.0"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            mode,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            executor,
            _ => null);

        Assert.True(DoctorHarness.CheckNamed(report, "codegraph.index").Passed);
        Assert.Single(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        Assert.DoesNotContain(
            executor.CallsTo("codegraph"),
            call => call.ArgumentList.Contains("index", StringComparer.Ordinal));
    }

    /// <summary>
    /// A <c>.codegraph/</c> folder that was never initialised makes <c>sync</c> and <c>index</c> both refuse. The fix the
    /// doctor prints must be the one that works, <c>codegraph init .</c>, not the command that just failed.
    /// </summary>
    [Fact]
    public void Ensure_FolderNeverInitialised_PrintsInitAsTheFix()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        ScriptedProcessExecutor executor = DoctorHarness.Executor(
            Root,
            arguments => arguments.Contains("--version", StringComparer.Ordinal)
                ? DoctorHarness.Ok("1.5.0")
                : new ProcessResult(1, string.Empty, "✗ CodeGraph not initialized in " + Root));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Ensure,
            DoctorHarness.Options(DoctorHarness.HealthyStub(_stubs.Path, Root)),
            executor,
            _ => null);

        DoctorCheck index = DoctorHarness.CheckNamed(report, "codegraph.index");
        Assert.False(index.Passed);
        Assert.Contains("not set up", index.Reason, StringComparison.Ordinal);
        Assert.Equal("codegraph init .", index.Fix);
    }

    /// <summary>
    /// A server whose <c>docs_answer_question_from_repo_documentation</c> returns an error result fails <c>mcp.docs_explore</c> with exit 1. Process
    /// liveness is not enough: <c>docs_get_source_repository_and_revision</c> alone would pass here, which is why D8 chose <c>docs_answer_question_from_repo_documentation</c>.
    /// </summary>
    [Fact]
    public void McpLiveProbe_DocsExploreError_Exits1()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ErroringDocsExploreStub(_stubs.Path, Root)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "mcp.docs_explore").Passed);
    }

    /// <summary>
    /// A server answering from another checkout fails <c>mcp.root</c> with exit 1, even though <c>docs_answer_question_from_repo_documentation</c>
    /// succeeded. The root check is what catches a server pointed at the wrong repository.
    /// </summary>
    /// <remarks>D8: the <c>provenance: root=</c> value must equal <c>git rev-parse --show-toplevel</c>.</remarks>
    [Fact]
    public void McpLiveProbe_RootMismatch_Exits1_McpRoot()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.WrongRootStub(_stubs.Path)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }

    /// <summary>
    /// The real corpus provenance line includes metadata after the root path, and the root still has to match exactly.
    /// Paths with spaces are legal and must not be truncated before comparison.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public void McpLiveProbe_ProvenanceRootWithMetadata_PassesWhenRootMatches(string dirty)
    {
        string root = Path.Combine(_root.Path, "repo with spaces");
        Directory.CreateDirectory(Path.Combine(root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ReplyingStub(
                _stubs.Path,
                DoctorHarness.InitializeReply,
                ProvenanceCall(root, dirty))),
            DoctorHarness.Executor(root),
            _ => null);

        Assert.Equal(0, report.ExitCode);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }

    /// <summary>
    /// When the server reports a different repository in the same metadata format, the doctor still fails the root check.
    /// </summary>
    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    public void McpLiveProbe_ProvenanceRootWithMetadata_MismatchFails(string dirty)
    {
        string root = Path.Combine(_root.Path, "repo with spaces");
        string wrongRoot = Path.Combine(_root.Path, "other repo with spaces");
        Directory.CreateDirectory(Path.Combine(root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ReplyingStub(
                _stubs.Path,
                DoctorHarness.InitializeReply,
                ProvenanceCall(wrongRoot, dirty))),
            DoctorHarness.Executor(root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }

    /// <summary>
    /// Older servers that emit only the bare root remain accepted for backward compatibility.
    /// </summary>
    [Fact]
    public void McpLiveProbe_BareProvenanceRoot_IsStillAccepted()
    {
        string root = Path.Combine(_root.Path, "bare repo");
        Directory.CreateDirectory(Path.Combine(root, ".codegraph"));

        string call = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\"provenance: root=" + root + "\"}]}}";

        DoctorReport report = RepositoryDoctor.Run(
            root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ReplyingStub(
                _stubs.Path,
                DoctorHarness.InitializeReply,
                call)),
            DoctorHarness.Executor(root),
            _ => null);

        Assert.Equal(0, report.ExitCode);
        Assert.True(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }

    /// <summary>
    /// A server that answers <c>initialize</c> and then closes its input fails a check with exit 1 instead of throwing
    /// out of the probe. The child's input is closed before it writes the reply, so the next write reaches a broken
    /// pipe rather than racing the child.
    /// </summary>
    /// <remarks>
    /// A live server can exit mid-conversation — a crash, a supervisor restart, a killed process. The probe is the
    /// thing that reports it, so a broken pipe must become a check line and not escape as an <c>IOException</c> that
    /// skips the renderer and breaks the 0/1/2/3 exit contract.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_ServerClosesInput_ReportsAFailureInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ClosesInputAfterInitializeStub(_stubs.Path), TimeSpan.FromSeconds(5)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Checks, check => !check.Passed);
    }

    /// <summary>
    /// A reply whose members are not the shapes the probe expects fails a check instead of throwing. The three cases
    /// are a JSON array in place of the result object, an array in place of the tool result, and a non-object item in
    /// the content array; each reaches a different <c>TryGetProperty</c>.
    /// </summary>
    /// <remarks>
    /// The reply is the child's data. <see cref="JsonElement.TryGetProperty"/> throws
    /// <see cref="InvalidOperationException"/> on a non-object, so without a kind check a malformed-but-parseable
    /// reply crashes the doctor rather than failing <c>mcp.handshake</c> or <c>mcp.docs_explore</c>.
    /// </remarks>
    [Theory]
    [InlineData("initialize-is-an-array")]
    [InlineData("result-is-an-array")]
    [InlineData("content-item-is-a-number")]
    public void McpLiveProbe_NonObjectReply_FailsACheckInsteadOfThrowing(string shape)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        string initialize = shape == "initialize-is-an-array"
            ? """[1,2]"""
            : DoctorHarness.InitializeReply;
        string call = shape switch
        {
            "result-is-an-array" => """{"jsonrpc":"2.0","id":2,"result":[42]}""",
            "content-item-is-a-number" => """{"jsonrpc":"2.0","id":2,"result":{"isError":false,"content":[42]}}""",
            _ => """{"jsonrpc":"2.0","id":2,"result":{"isError":false,"content":[{"type":"text","text":"provenance: root=/elsewhere"}]}}""",
        };

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ReplyingStub(_stubs.Path, initialize, call)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.Contains(report.Checks, check => !check.Passed);
    }

    /// <summary>
    /// A provenance root the platform cannot canonicalise fails <c>mcp.root</c> instead of throwing. The server's
    /// text is untrusted: a NUL inside it makes <see cref="System.IO.Path.GetFullPath(string)"/> throw.
    /// </summary>
    /// <remarks>
    /// The value is server-controlled, so the probe must survive any string that parses as JSON. The JSON escape
    /// <c>\u0000</c> decodes to a NUL character, which reaches the path call and throws <c>ArgumentException</c>.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_ProvenanceRootWithNulChar_FailsMcpRootInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        const string call = """{"jsonrpc":"2.0","id":2,"result":{"isError":false,"content":[{"type":"text","text":"provenance: root=/tmp/ev\u0000il"}]}}""";

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.ReplyingStub(_stubs.Path, DoctorHarness.InitializeReply, call)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }

    /// <summary>
    /// A server that cannot start fails <c>mcp.start</c>, and no check reason or fix line echoes the environment. The
    /// secret is injected through the environment delegate, so the test never touches the process environment.
    /// </summary>
    /// <remarks>D9: a failure never echoes the child's environment or stderr beyond the first error line.</remarks>
    [Fact]
    public void McpLiveProbe_StartFailure_DoesNotDiscloseEnvironment()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options("kyber-weave-mcp-absent-for-test"),
            DoctorHarness.Executor(Root),
            name => name == "KW_DOCTOR_TEST_SECRET" ? Secret : null);

        Assert.False(DoctorHarness.CheckNamed(report, "mcp.start").Passed);
        Assert.Equal(1, report.ExitCode);
        foreach (DoctorCheck check in report.Checks)
        {
            Assert.DoesNotContain(Secret, check.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, check.Fix ?? string.Empty, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A server whose reply exceeds the session's line cap fails <c>mcp.handshake</c> instead of throwing.
    /// </summary>
    /// <remarks>
    /// The cap is the session's own classification of a child it must stop reading, and the session reports it as
    /// <see cref="InvalidDataException"/>. A reply is the child's data, so an unterminated one must reach the same
    /// check line as any other bad reply. Escaping <see cref="RepositoryDoctor.Run"/> would skip the renderer and
    /// break the documented 0/1/2/3 exit contract, which is the whole point of the probe.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_ReplyOverTheLineCap_FailsMcpHandshakeInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        string stub = DoctorHarness.WriteStub(
            _stubs.Path,
            """
            read _initialize
            head -c 2000000 /dev/zero | tr '\0' 'a'
            sleep 30
            """);

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(stub, TimeSpan.FromSeconds(30)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(1, report.ExitCode);
        Assert.False(DoctorHarness.CheckNamed(report, "mcp.handshake").Passed);
    }

    /// <summary>
    /// A budget too large to double inside an <see cref="int"/> still prints a positive timeout in the remedy.
    /// </summary>
    /// <remarks>
    /// The remedy is a command a human copies and runs, so it must be one the option accepts. <c>--mcp-timeout</c>
    /// takes any positive int, and doubling one of those in int wraps negative: the fix line would then instruct the
    /// reader to pass a timeout the parser refuses. The hint is capped where the parser's own limit is.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_HugeBudget_PrintsAPositiveTimeoutInTheRemedy()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(
                DoctorHarness.WriteStub(
                    _stubs.Path,
                    $"read _initialize\nprintf '%s\\n' '{DoctorHarness.InitializeReply}'\nread _initialized\nread _call\nexit 0"),
                TimeSpan.FromSeconds(2_000_000_000)),
            DoctorHarness.Executor(Root),
            _ => null);

        DoctorCheck docsExplore = DoctorHarness.CheckNamed(report, "mcp.docs_explore");
        Assert.False(docsExplore.Passed);
        string fix = docsExplore.Fix ?? string.Empty;
        int marker = fix.IndexOf("--mcp-timeout ", StringComparison.Ordinal);
        Assert.True(marker >= 0, $"The remedy '{fix}' does not name a timeout to raise.");
        string suggested = fix[(marker + "--mcp-timeout ".Length)..].Trim();
        Assert.True(
            int.TryParse(suggested, out int seconds) && seconds > 0,
            $"The remedy suggested '{suggested}', which is not a timeout --mcp-timeout accepts.");
    }

    /// <summary>
    /// The probe starts the server with its CodeGraph refresh disabled, so <c>doctor --check</c> keeps
    /// the promise its own description makes.
    /// </summary>
    /// <remarks>
    /// D23: <c>--check</c> says it "never runs init or index", but the probe makes a real docs call, and
    /// the server now refreshes CodeGraph on its first tool call. Without the flag the read-only verb
    /// would write an index, and every read-only caller — run start, CI, a reviewer — would inherit a
    /// write it was told would not happen.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_StartsTheServerWithCodeGraphRefreshDisabled()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        string argvPath = Path.Combine(_stubs.Path, "argv.txt");

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(DoctorHarness.RecordingStub(_stubs.Path, Root, argvPath)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.Equal(0, report.ExitCode);
        Assert.Contains("--no-codegraph-refresh", File.ReadAllLines(argvPath));
    }

    /// <summary>
    /// A provenance line carrying the branch and repository fields still passes <c>mcp.root</c> when the
    /// root is this checkout.
    /// </summary>
    /// <remarks>
    /// D24: the header gained <c>branch=</c> and <c>repo=</c>. The probe compares the served root to the
    /// resolved one after stripping the metadata that follows it, so a metadata field it does not know
    /// becomes part of the path it compares — and a healthy server answering for this very repository
    /// fails the check. That is the failure this case prevents: the added fields must not turn a correct
    /// server into a "wrong repository" diagnosis.
    /// </remarks>
    [Fact]
    public void McpLiveProbe_ProvenanceWithBranchAndRepoMetadata_StillPassesMcpRoot()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".codegraph"));
        string served = "provenance: root=" + Root
            + " branch=main repo=" + Path.Combine(Root, ".git")
            + " rev=abc123 dirty=no documents=7";
        string call = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\""
            + served
            + "\"}]}}";

        DoctorReport report = RepositoryDoctor.Run(
            Root,
            DoctorMode.Check,
            DoctorHarness.Options(
                DoctorHarness.ReplyingStub(_stubs.Path, DoctorHarness.InitializeReply, call)),
            DoctorHarness.Executor(Root),
            _ => null);

        Assert.True(
            DoctorHarness.CheckNamed(report, "mcp.root").Passed,
            $"A server answering '{served}' names this repository's root, so mcp.root must pass. "
            + $"It reported: {DoctorHarness.CheckNamed(report, "mcp.root").Reason}");
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// A provenance line carrying a repository path with spaces in repo= still passes mcp.root.
    /// </summary>
    [Fact]
    public void McpLiveProbe_ProvenanceWithSpacedRootAndRepo_StillPassesMcpRoot()
    {
        string spacedRoot = Path.Combine(Root, "repo with spaces");
        Directory.CreateDirectory(Path.Combine(spacedRoot, ".codegraph"));
        string served = "provenance: root=" + spacedRoot
            + " branch=main repo=" + Path.Combine(spacedRoot, ".git")
            + " rev=abc123 dirty=no documents=7";
        string call = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\""
            + served
            + "\"}]}}";

        DoctorReport report = RepositoryDoctor.Run(
            spacedRoot,
            DoctorMode.Check,
            DoctorHarness.Options(
                DoctorHarness.ReplyingStub(_stubs.Path, DoctorHarness.InitializeReply, call)),
            DoctorHarness.Executor(spacedRoot),
            _ => null);

        Assert.True(
            DoctorHarness.CheckNamed(report, "mcp.root").Passed,
            $"A server answering '{served}' names this repository's root, so mcp.root must pass. "
            + $"It reported: {DoctorHarness.CheckNamed(report, "mcp.root").Reason}");
        Assert.Equal(0, report.ExitCode);
    }

    /// <summary>
    /// A repository path containing token-like substrings like "rev=evil" in root and repo
    /// is not truncated by the metadata stripper.
    /// </summary>
    [Fact]
    public void McpLiveProbe_ProvenancePathContainingTokenSubstrings_StillPassesMcpRoot()
    {
        string evilRoot = Path.Combine(Root, "my rev=evil");
        Directory.CreateDirectory(Path.Combine(evilRoot, ".codegraph"));
        string served = "provenance: root=" + evilRoot
            + " branch=main repo=" + Path.Combine(evilRoot, ".git")
            + " rev=abc123 dirty=no documents=7";
        string call = "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\""
            + served
            + "\"}]}}";

        DoctorReport report = RepositoryDoctor.Run(
            evilRoot,
            DoctorMode.Check,
            DoctorHarness.Options(
                DoctorHarness.ReplyingStub(_stubs.Path, DoctorHarness.InitializeReply, call)),
            DoctorHarness.Executor(evilRoot),
            _ => null);

        Assert.True(
            DoctorHarness.CheckNamed(report, "mcp.root").Passed,
            $"A server answering '{served}' names this repository's root, so mcp.root must pass. "
            + $"It reported: {DoctorHarness.CheckNamed(report, "mcp.root").Reason}");
        Assert.Equal(0, report.ExitCode);
    }

    private static string ProvenanceCall(string root, string dirty, string rev = "abc123", int documents = 7) =>
        "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\"provenance: root=" + root + " rev=" + rev + " dirty=" + dirty + " documents=" + documents + "\"}]}}";
}
