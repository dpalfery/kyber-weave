using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>docs mcp-wire</c> and <c>docs mcp-unwire</c> surface (R5.1–R5.3, R5.10, R6.2): who is
/// written, who is only reported, what the exit code says, and what a preview, a refusal and a
/// removal leave on disk.
/// </summary>
/// <remarks>
/// <para>
/// Every case builds a repository tree in a temp folder and drives the command directly, then reads
/// the result from disk. What is asserted is the observable outcome — the bytes that appeared, the
/// bytes that did not, the receipt's records, the exit code and the diagnostic the operator is given
/// — never the command's private plumbing.
/// </para>
/// <para>
/// No case here mutates <c>PATH</c>, and no case resolves a launch binary through the machine's own
/// search path. A declaration is allowed to name an absolute command (<c>argv[0]</c> is a path, which
/// <see cref="LaunchCommandResolver"/> resolves as given), so the suite puts a fake binary in the temp
/// tree and declares that path. <c>Environment.SetEnvironmentVariable</c> is process-wide state that
/// tests in other collections running in parallel would observe, so resolving through the environment
/// is a thing these tests must not do to make a server look wired (test standard, "Concurrency and
/// zero-flakiness isolation").
/// </para>
/// <para>
/// Two properties earn these tests their keep. <b>Selection is consent</b>: a named harness is written
/// even where no marker suggests it is in use, because for Claude Code and opencode the only
/// repository-scope artefact is the configuration file itself, so "not in play" and "file absent" are
/// the same condition (R5.4, D19). <b>Nothing is written without a name</b>: a run with no target is a
/// preview that writes not one byte, receipt included (R5.3, R5.10).
/// </para>
/// </remarks>
public sealed class DocsMcpWireCommandTests : IDisposable
{
    private const string ServerName = "kyber-weave";

    /// <summary>The second server a two-server declaration names, and the file its command names.</summary>
    private const string SecondServerName = "codegraph";

    private const string SecondBinaryName = "fake-codegraph";

    private const string Factory = "factory";

    private const string ClaudeCode = "claude-code";

    private const string VsCode = "vscode";

    private const string OpenCode = "opencode";

    private const string Codex = "codex";

    /// <summary>The repair refused a requested target and wrote nothing to it.</summary>
    private const string RefusalCode = "KW-MCP-WIRE-005";

    /// <summary>Removal was refused: no record, or the file changed since the write (R5.9).</summary>
    private const string DriftCode = "KW-MCP-WIRE-006";

    /// <summary>The host configuration could not be read; the existing configuration failure path.</summary>
    private const string ConfigErrorCode = "KW-CONFIG-001";

    /// <summary>
    /// The "no target selected" spelling a caller of the settings type uses. The command treats an
    /// empty list as no selection (R5.3), which is also what the option's default arrives as.
    /// </summary>
    private const string NoTarget = "";

    /// <summary>
    /// The escape sequence the hostile-input cases carry: SGR red, which a terminal obeys and no
    /// operator wrote. Both the declaration and a harness's own file are untrusted input, and a
    /// control character in either reaches the terminal unless the rendering path removes it.
    /// </summary>
    private const string EscapeSequence = "\u001b[31m";

    private readonly TempDirectory _root = new();

    public DocsMcpWireCommandTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Bin);
        McpWiringFixture.WriteExecutable(Bin, "fake-kyber-weave-mcp");
    }

    private string Repo => Path.Combine(_root.Path, "repo");

    /// <summary>Where the fake launch binary lives: never on <c>PATH</c>, always declared by path.</summary>
    private string Bin => Path.Combine(_root.Path, "bin");

    /// <summary>
    /// The declared command. A path rather than a name, so the declaration resolves on every machine
    /// without the test reaching into the process environment.
    /// </summary>
    private string BinaryPath => Path.Combine(Bin, "fake-kyber-weave-mcp");

    /// <summary>The second declared server, and the binary its command names.</summary>
    private string SecondBinaryPath => Path.Combine(Bin, SecondBinaryName);

    /// <summary>The receipt the design fixes at this path (DD13), asserted as a literal.</summary>
    private string ReceiptPath =>
        Path.Combine(Repo, ".kyber-weave", "receipts", "mcp-wiring.json");

    public void Dispose() => _root.Dispose();

    /// <summary>
    /// With no target selected the command is a preview: it reports what it would do, writes nothing —
    /// not the configuration file and not the receipt — and exits 0, because refusing to write is the
    /// documented outcome of asking it to write nothing (R5.3).
    /// </summary>
    [Fact]
    public void NoTargetPreviewWritesNothingAndExitsZero()
    {
        WriteDeclaration();
        Directory.CreateDirectory(Path.Combine(Repo, ".factory"));

        CapturedConsoleExecution<int> execution = Wire(NoTarget);

        Assert.Equal(0, execution.Result);
        Assert.False(
            File.Exists(Path.Combine(Repo, ".factory", "mcp.json")),
            "a preview must not create the configuration file it reports on");
        Assert.False(File.Exists(ReceiptPath), "a preview must not write the receipt");
        Assert.Contains(Factory, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The no-target preview names each writable harness no marker can detect in this tree. Naming it
    /// is the only way it can ever be written, because selection is the operator's consent (R5.3): a
    /// harness that is neither in play by marker nor present as a file would otherwise be invisible.
    /// </summary>
    [Fact]
    public void NoTargetPreviewNamesEachWritableHarnessNoMarkerCanDetect()
    {
        WriteDeclaration();

        // Claude Code and opencode carry no marker directory at all: the file is the only signal, so
        // an absent file leaves them undetectable, and the preview has to name them for the operator.
        Assert.Empty(HarnessMcpCatalog.Find(ClaudeCode)!.MarkerDirectories);
        Assert.Empty(HarnessMcpCatalog.Find(OpenCode)!.MarkerDirectories);

        CapturedConsoleExecution<int> execution = Wire(NoTarget);

        Assert.Equal(0, execution.Result);
        Assert.Contains(ClaudeCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(OpenCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains("mcp-wire", execution.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(InRepo(".mcp.json")));
        Assert.False(File.Exists(InRepo("opencode.json")));
    }

    /// <summary>
    /// A named harness is written: the file appears holding the declared server, and the receipt
    /// records the write so the removal path has something to undo (R5.1, R5.4).
    /// </summary>
    [Fact]
    public void TargetedWriteCreatesTheFileAndTheReceipt()
    {
        WriteDeclaration();

        CapturedConsoleExecution<int> execution = Wire(Factory);

        Assert.Equal(0, execution.Result);

        string path = Path.Combine(Repo, ".factory", "mcp.json");
        Assert.True(File.Exists(path), "a named target must be written");

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            JsonElement servers = document.RootElement.GetProperty("mcpServers");
            Assert.True(
                servers.TryGetProperty(ServerName, out JsonElement entry),
                $"the created file must hold the declared server, but held {servers.GetRawText()}");
            Assert.Equal(BinaryPath, ExecutableOf(entry));
        }

        Assert.True(File.Exists(ReceiptPath), "a real run records what it wrote");
        Assert.NotNull(McpWiringReceipt.Load(Repo).Find(".factory/mcp.json"));
    }

    /// <summary>
    /// A refused target exits 1, names the file and the rule id, and leaves the file byte-identical:
    /// an entry the repair will not modify is refused rather than merged over (design, error table;
    /// R5.5). The refusal is an error outcome of the operation the operator asked for.
    /// </summary>
    [Fact]
    public void ARefusedTargetExitsOneWithTheRuleIdAndLeavesTheFileAlone()
    {
        WriteDeclaration();
        WriteJson(
            ".vscode/mcp.json",
            new JsonObject
            {
                ["servers"] = new JsonObject
                {
                    [ServerName] = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = "kyber-weave-mcp-other",
                        ["args"] = new JsonArray("--repo-root", ".")
                    }
                }
            });
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".vscode", "mcp.json"));

        CapturedConsoleExecution<int> execution = Wire(VsCode);

        Assert.Equal(1, execution.Result);
        Assert.Contains(RefusalCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains("mcp.json", execution.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Repo, ".vscode", "mcp.json")));
        Assert.Null(McpWiringReceipt.Load(Repo).Find(".vscode/mcp.json"));
    }

    /// <summary>
    /// A dry run computes the same change, prints it, writes nothing at all — no configuration file,
    /// no receipt — and exits what the real run exits (R5.10).
    /// </summary>
    [Fact]
    public void DryRunWritesNothingIncludingTheReceiptAndExitsWhatTheRealRunExits()
    {
        WriteDeclaration();

        CapturedConsoleExecution<int> dryRun = Wire(Factory, dryRun: true);

        Assert.False(File.Exists(Path.Combine(Repo, ".factory", "mcp.json")), "a dry run writes nothing");
        Assert.False(File.Exists(ReceiptPath), "a dry run writes no receipt, not even one");
        Assert.Contains("mcp.json", dryRun.Output, StringComparison.Ordinal);

        CapturedConsoleExecution<int> real = Wire(Factory);

        Assert.Equal(real.Result, dryRun.Result);
        Assert.True(File.Exists(Path.Combine(Repo, ".factory", "mcp.json")));
    }

    /// <summary>
    /// A dry run of a target that would be refused exits what the real run exits, which is 1: the
    /// preview is the same computation, and holding back the bytes must not soften the verdict.
    /// </summary>
    [Fact]
    public void DryRunOfARefusedTargetExitsOneAndStillWritesNothing()
    {
        WriteDeclaration();
        WriteJson(
            ".vscode/mcp.json",
            new JsonObject
            {
                ["servers"] = new JsonObject
                {
                    [ServerName] = new JsonObject { ["command"] = "kyber-weave-mcp-other" }
                }
            });
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".vscode", "mcp.json"));

        CapturedConsoleExecution<int> execution = Wire(VsCode, dryRun: true);

        Assert.Equal(1, execution.Result);
        Assert.Contains(RefusalCode, execution.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Repo, ".vscode", "mcp.json")));
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// Removal restores what the repair wrote and exits 0: an inserted file comes back byte-for-byte,
    /// and a file the repair created is deleted, both driven by the receipt (R5.8).
    /// </summary>
    [Fact]
    public void UnwireRestoresTheFilesTheRepairWroteAndExitsZero()
    {
        WriteDeclaration();
        byte[] original = Encoding.UTF8.GetBytes("""{"mcpServers": {"other": {"command": "other-mcp"}}}""");
        McpWiringFixture.WriteBytes(Repo, ".factory/mcp.json", original);

        CapturedConsoleExecution<int> wire = Wire($"{Factory},{ClaudeCode}");
        Assert.Equal(0, wire.Result);
        Assert.True(File.Exists(Path.Combine(Repo, ".mcp.json")), "claude-code must be created");
        Assert.NotNull(McpWiringReceipt.Load(Repo).Find(".mcp.json"));

        CapturedConsoleExecution<int> unwire = Unwire($"{Factory},{ClaudeCode}");

        Assert.Equal(0, unwire.Result);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Repo, ".factory", "mcp.json")));
        Assert.False(File.Exists(Path.Combine(Repo, ".mcp.json")), "a file the repair created is deleted");

        McpWiringReceipt receipt = McpWiringReceipt.Load(Repo);
        Assert.Null(receipt.Find(".factory/mcp.json"));
        Assert.Null(receipt.Find(".mcp.json"));
    }

    /// <summary>
    /// Removal refuses over a foreign edit rather than restoring over it, and says so with the drift
    /// rule id and a non-zero exit (R5.9, D14). Overwriting someone else's change is the failure this
    /// refusal exists to prevent, so the file is asserted to still hold it.
    /// </summary>
    [Fact]
    public void UnwireRefusesOverAForeignEditAndExitsOne()
    {
        WriteDeclaration();
        WriteJson(
            ".factory/mcp.json",
            new JsonObject
            {
                ["mcpServers"] = new JsonObject { ["other"] = new JsonObject { ["command"] = "other-mcp" } }
            });

        Assert.Equal(0, Wire(Factory).Result);

        string path = Path.Combine(Repo, ".factory", "mcp.json");
        string handEdited = File.ReadAllText(path) + "\n// edited by hand after the repair\n";
        File.WriteAllText(path, handEdited);

        CapturedConsoleExecution<int> unwire = Unwire(Factory);

        Assert.Equal(1, unwire.Result);
        Assert.Contains(DriftCode, unwire.Output, StringComparison.Ordinal);
        Assert.Contains("mcp.json", unwire.Output, StringComparison.Ordinal);
        Assert.Contains("edited by hand after the repair", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// A requested harness the repair never writes is refused with the snippet to paste instead, and
    /// exits 1: Codex's entry body is unverified, so an invented one is worse than a manual step
    /// (DD11). Nothing is created for it (R5.2 — repository-scope files only, and it has none to write).
    /// </summary>
    [Fact]
    public void ASnippetOnlyTargetExitsOneWithTheSnippet()
    {
        WriteDeclaration();

        CapturedConsoleExecution<int> execution = Wire(Codex);

        Assert.Equal(1, execution.Result);
        Assert.Contains(RefusalCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains($"[mcp_servers.{ServerName}]", execution.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Repo, ".codex", "config.toml")));
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// A named harness that has nothing to write — its file already holds every declared server — is
    /// reported and skipped, not failed: there is no gap, so exit 0 and the file is untouched
    /// (design §5, "reported and skipped"). A repair that refused here would make a second run of the
    /// same command fail on its own earlier success.
    /// </summary>
    [Fact]
    public void ARequestedHarnessAlreadyHoldingEveryDeclaredServerIsSkippedAndExitsZero()
    {
        WriteDeclaration();
        WriteJson(
            ".mcp.json",
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [ServerName] = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = BinaryPath,
                        ["args"] = new JsonArray("--repo-root", ".")
                    }
                }
            });
        byte[] before = File.ReadAllBytes(Path.Combine(Repo, ".mcp.json"));

        CapturedConsoleExecution<int> execution = Wire(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Repo, ".mcp.json")));
        Assert.Null(McpWiringReceipt.Load(Repo).Find(".mcp.json"));
        Assert.Contains(ClaudeCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A named harness whose file is absent is created and exits 0, even though no marker puts it in
    /// play. For Claude Code and opencode the configuration file is the only repository-scope
    /// artefact, so "not in play" and "file absent" are the same condition, and the operator naming
    /// the harness is the consent the write needs (R5.4, D19).
    /// </summary>
    [Theory]
    [InlineData(ClaudeCode, ".mcp.json", "mcpServers")]
    [InlineData(OpenCode, "opencode.json", "mcp")]
    public void AnAbsentTargetFileIsCreatedForAHarnessNoMarkerCanDetect(
        string harness,
        string configFile,
        string serverKey)
    {
        WriteDeclaration();

        CapturedConsoleExecution<int> execution = Wire(harness);

        Assert.Equal(0, execution.Result);

        string path = InRepo(configFile);
        Assert.True(File.Exists(path), $"{harness} must be created when it is named");

        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            JsonElement servers = document.RootElement.GetProperty(serverKey);
            Assert.True(
                servers.TryGetProperty(ServerName, out JsonElement entry),
                $"{harness} must hold the declared server under '{serverKey}', but held {servers.GetRawText()}");
            Assert.Equal(BinaryPath, ExecutableOf(entry));
        }

        Assert.NotNull(McpWiringReceipt.Load(Repo).Find(configFile));
    }

    /// <summary>
    /// A declaration the loader rejects fails the command through the existing configuration failure
    /// path (<c>KW-CONFIG-001</c>) and writes nothing: a repair that guessed at a malformed
    /// declaration would wire a server the repository never successfully declared (design §3.6).
    /// </summary>
    [Fact]
    public void AMalformedDeclarationFailsThroughTheConfigurationErrorPath()
    {
        WriteDeclaration(provenance: "guesswork");

        CapturedConsoleExecution<int> execution = Wire(Factory);

        Assert.Equal(1, execution.Result);
        Assert.Contains(ConfigErrorCode, execution.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Repo, ".factory", "mcp.json")));
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// A target that names no harness is refused with the rule id and exit 1, rather than quietly
    /// doing nothing: a slug the operator mistyped is the one case where "wrote nothing" and "did
    /// what was asked" look identical from the outside, and the run would report success (design,
    /// error table; D14).
    /// </summary>
    [Fact]
    public void AnUnknownTargetIsRefusedAndExitsOne()
    {
        WriteDeclaration();

        CapturedConsoleExecution<int> execution = Wire("claude");

        Assert.Equal(1, execution.Result);
        Assert.Contains(RefusalCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains("claude", execution.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(InRepo(".mcp.json")));
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// The dry run and the real run both name every server the write touches.
    /// </summary>
    /// <remarks>
    /// R5.10 says the preview prints "the exact per-file change", and the change is which servers the
    /// file gains: a line naming only the path and the harness asks the operator to consent to a write
    /// they cannot see, in a file the whole team shares. The real run is asserted too, because a
    /// preview that describes the change while the write it previews reports only the file is the
    /// same omission one step later.
    /// </remarks>
    [Fact]
    public void TheDryRunAndTheRealRunNameEveryServerTheWriteAdds()
    {
        WriteTwoServerDeclaration();

        CapturedConsoleExecution<int> dryRun = Wire(Factory, dryRun: true);

        Assert.Equal(0, dryRun.Result);
        Assert.False(File.Exists(InRepo(".factory/mcp.json")), "a dry run writes nothing");
        Assert.True(
            dryRun.Output.Contains(ServerName, StringComparison.Ordinal)
                && dryRun.Output.Contains(SecondServerName, StringComparison.Ordinal),
            $"The preview named the file but not the servers it would add. Output:{Environment.NewLine}{dryRun.Output}");

        CapturedConsoleExecution<int> real = Wire(Factory);

        Assert.Equal(0, real.Result);
        Assert.True(
            real.Output.Contains(ServerName, StringComparison.Ordinal)
                && real.Output.Contains(SecondServerName, StringComparison.Ordinal),
            $"The run named the file but not the servers it added. Output:{Environment.NewLine}{real.Output}");
    }

    /// <summary>
    /// A second run of the same target exits 0 and writes nothing.
    /// </summary>
    /// <remarks>
    /// This is the second-run contract the corpus documents and the reason the check and the repair
    /// have to agree: the repair refuses a file that already holds the server's name (5.5), so the
    /// discriminator between "somebody else owns this entry" and "this is the entry we wrote" is
    /// whether the file already holds exactly what is declared. VS Code is the case that broke it —
    /// its entry carries the harness's own <c>${workspaceFolder}</c> substitution, which 5.11 requires
    /// — so a second run saw a difference, went to the repair, and was refused by the file it had
    /// written moments earlier.
    /// </remarks>
    [Fact]
    public void ASecondRunOfTheSameTargetWritesNothingAndExitsZero()
    {
        WriteDeclaration();

        CapturedConsoleExecution<int> first = Wire(VsCode);
        Assert.Equal(0, first.Result);

        string path = InRepo(".vscode/mcp.json");
        Assert.True(File.Exists(path), $"the first run wrote no {path}. Output:{Environment.NewLine}{first.Output}");
        byte[] written = File.ReadAllBytes(path);

        CapturedConsoleExecution<int> second = Wire(VsCode);

        Assert.Equal(0, second.Result);
        Assert.DoesNotContain(RefusalCode, second.Output, StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A host configuration that says nothing about MCP is a repository declaring no server, so the
    /// command reports that and exits 0. It is not a configuration failure — the file loads, it just
    /// has no <c>mcp:</c> section — and it is not a dereference of a section that is not there.
    /// </summary>
    [Fact]
    public void AConfigurationWithNoMcpSectionReportsNothingToWireAndExitsZero()
    {
        Write(".kyber-weave/kyber-weave.yml", "ontology:\n  docs-root: docs\n");

        CapturedConsoleExecution<int> execution = Wire(Factory);

        Assert.Equal(0, execution.Result);
        Assert.Contains("nothing to wire", execution.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Repo, ".factory", "mcp.json")));
        Assert.False(File.Exists(ReceiptPath));
    }

    /// <summary>
    /// A server name carrying an escape sequence reaches the operator as the escaped TOML key the
    /// declaration spells, and never as a control character the terminal would act on.
    /// </summary>
    /// <remarks>
    /// The two halves of this are one guarantee each, and neither may drift into the other. The
    /// snippet is written raw because Spectre markup would eat its brackets, so the rendering path
    /// is what stops a control character reaching the terminal. The key, though, is the declared
    /// name — control character and all — escaped for TOML rather than stripped: the operator
    /// pastes this snippet into a configuration file, and a name that had been stripped would not
    /// match the declaration, so the check would go on reporting the server as unwired against the
    /// entry the operator had just made by hand.
    /// </remarks>
    [Fact]
    public void AServerNameCarryingAnEscapeSequenceReachesTheSnippetEscapedRatherThanRaw()
    {
        // The YAML carries the escape as its own \x1b escape, so the declaration on disk is
        // printable and the loader is what turns it into the control character.
        Write(
            ".kyber-weave/kyber-weave.yml",
            "mcp:\n"
            + "  required-servers:\n"
            + "    \"evil\\x1b[31mserver\":\n"
            + $"      command: ['{BinaryPath}', --repo-root, .]\n"
            + "      provenance: first-party\n"
            + "      level: required\n");

        CapturedConsoleExecution<int> execution = Wire(Codex);

        Assert.Equal(1, execution.Result);
        Assert.DoesNotContain("\u001b", execution.Output, StringComparison.Ordinal);
        Assert.Contains(
            "[mcp_servers.\"evil\\u001B[31mserver\"]",
            execution.Output,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A finding's hint and message quote the declared command and the wired one, and both come from
    /// a file: the declaration from <c>kyber-weave.yml</c>, the wired entry from the harness's own
    /// configuration. A token carrying an escape sequence or a line break must not reach the
    /// terminal, or the operator's console — not the finding — is what the file describes.
    /// </summary>
    [Fact]
    public void CommandsCarryingEscapeSequencesAreRemovedFromTheFinding()
    {
        Write(
            ".kyber-weave/kyber-weave.yml",
            "mcp:\n"
            + "  required-servers:\n"
            + $"    {ServerName}:\n"
            + $"      command: ['{BinaryPath}', \"--token\", \"evil\\x1b[31mtok\"]\n"
            + "      provenance: first-party\n"
            + "      level: required\n");

        WriteJson(
            ".mcp.json",
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [ServerName] = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = "wired" + EscapeSequence + "binary",
                        ["args"] = new JsonArray("--repo-root", ".")
                    }
                }
            });

        CapturedConsoleExecution<int> execution = Wire(NoTarget);

        Assert.Equal(0, execution.Result);
        Assert.Contains(McpWiringInspector.CommandDiffersCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", execution.Output, StringComparison.Ordinal);
        Assert.Contains("eviltok", execution.Output, StringComparison.Ordinal);
        Assert.Contains("wiredbinary", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A two-character escape — the charset shift a terminal itself emits — is removed whole. Its
    /// final byte is not text, so dropping only the introducer would print a character the operator
    /// never wrote.
    /// </summary>
    [Fact]
    public void ATwoCharacterEscapeIsRemovedRatherThanLeavingItsFinalByte()
    {
        Assert.Equal("kept", TerminalText.Line("kept\u001b(B"));
    }

    /// <summary>
    /// A launch command written as a repository-relative path is resolved against the repository
    /// root, not against the directory the operator happened to run this command from.
    /// </summary>
    /// <remarks>
    /// The declaration is committed and the binary it names lives in the tree, so the answer must
    /// not depend on the shell's working directory: run from the repository it names a file, and run
    /// from anywhere else the same declaration names the same file. This case runs from the test
    /// process's own working directory — never the fixture's repository — so a resolver that read
    /// the process's directory would report the server unlaunchable and write nothing.
    /// </remarks>
    [Fact]
    public void ARepositoryRelativeLaunchCommandResolvesAgainstTheRepositoryRoot()
    {
        McpWiringFixture.WriteExecutable(Path.Combine(Repo, "tools"), "serve-mcp");
        Write(
            ".kyber-weave/kyber-weave.yml",
            "mcp:\n"
            + "  required-servers:\n"
            + "    tools-server:\n"
            + "      command: [tools/serve-mcp, --repo-root, .]\n"
            + "      provenance: third-party\n"
            + "      level: required\n");

        Assert.NotEqual(
            Path.GetFullPath(Repo),
            Path.GetFullPath(Directory.GetCurrentDirectory()));

        CapturedConsoleExecution<int> execution = Wire(ClaudeCode);

        Assert.Equal(0, execution.Result);

        string path = InRepo(".mcp.json");
        Assert.True(
            File.Exists(path),
            $"a command relative to the repository root was not resolved against it. Output:{Environment.NewLine}{execution.Output}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(
            document.RootElement
                .GetProperty("mcpServers")
                .TryGetProperty("tools-server", out _),
            "the server the declaration names must be the entry the write added");
    }

    /// <summary>
    /// A value long enough to flood the console is cut, and the cut is marked. The length is part of
    /// the same bound as the escape sequences: the operator asked for a finding, not for one
    /// harness entry to push the finding and the remedy above it off the scrollback. The entry comes
    /// from the harness's own file, which has no cap of its own — unlike a YAML key, which YAML
    /// refuses past 1024 characters before this command ever sees it.
    /// </summary>
    [Fact]
    public void AWiredCommandLongEnoughToFloodTheTerminalIsCapped()
    {
        WriteDeclaration();
        string floodedCommand = new('y', 2000);
        WriteJson(
            ".mcp.json",
            new JsonObject
            {
                ["mcpServers"] = new JsonObject
                {
                    [ServerName] = new JsonObject
                    {
                        ["type"] = "stdio",
                        ["command"] = floodedCommand
                    }
                }
            });

        CapturedConsoleExecution<int> execution = Wire(NoTarget);

        Assert.Equal(0, execution.Result);
        Assert.Contains(McpWiringInspector.CommandDiffersCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(floodedCommand, execution.Output, StringComparison.Ordinal);
        Assert.Contains("…", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removal restores a file it had inserted into, so the line it prints says the servers are being
    /// put back — not that they are being removed. An operator who asked to undo a change was
    /// otherwise told the opposite of what happened, and the exit code could not correct it.
    /// </summary>
    [Fact]
    public void ARestoreIsLabelledRestoringRatherThanRemoving()
    {
        WriteDeclaration();
        McpWiringFixture.WriteBytes(
            Repo,
            ".factory/mcp.json",
            Encoding.UTF8.GetBytes("""{"mcpServers": {"other": {"command": "other-mcp"}}}"""));

        Assert.Equal(0, Wire(Factory).Result);

        CapturedConsoleExecution<int> unwire = Unwire(Factory);

        Assert.Equal(0, unwire.Result);
        Assert.Contains("restoring", unwire.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("removing", unwire.Output, StringComparison.Ordinal);
    }

    private CapturedConsoleExecution<int> Wire(string targets, bool dryRun = false) =>
        ProcessConsoleCapture.Run(() => new DocsMcpWireCommand().Execute(
            null!,
            new DocsMcpWireSettings { Path = Repo, Target = targets, DryRun = dryRun }));

    private CapturedConsoleExecution<int> Unwire(string targets) =>
        ProcessConsoleCapture.Run(() => new DocsMcpUnwireCommand().Execute(
            null!,
            new DocsMcpUnwireSettings { Path = Repo, Target = targets }));

    /// <summary>
    /// Writes the <c>mcp:</c> declaration in the shape <c>docs init</c> scaffolds, with the command
    /// given as an absolute path so the launch binary resolves without consulting <c>PATH</c>.
    /// </summary>
    private void WriteDeclaration(string provenance = "first-party") =>
        Write(
            ".kyber-weave/kyber-weave.yml",
            $$"""
            mcp:
              required-servers:
                {{ServerName}}:
                  command: ['{{BinaryPath}}', --repo-root, .]
                  provenance: {{provenance}}
                  level: required
            """);

    /// <summary>
    /// The same declaration with two servers, so a case can tell "one line about the file" from "the
    /// servers that file gains". The second is third-party and recommended, which is the shape a
    /// repository adopting this feature actually declares: one required first-party server and one
    /// optional helper.
    /// </summary>
    private void WriteTwoServerDeclaration()
    {
        McpWiringFixture.WriteExecutable(Bin, SecondBinaryName);

        Write(
            ".kyber-weave/kyber-weave.yml",
            $$"""
            mcp:
              required-servers:
                {{ServerName}}:
                  command: ['{{BinaryPath}}', --repo-root, .]
                  provenance: first-party
                  level: required
                {{SecondServerName}}:
                  command: ['{{SecondBinaryPath}}', serve, --mcp]
                  provenance: third-party
                  level: recommended
            """);
    }

    private void WriteJson(string relativePath, JsonObject document) =>
        Write(relativePath, document.ToJsonString());

    private void Write(string relativePath, string content) =>
        McpWiringFixture.WriteText(Repo, relativePath, content);


    /// <summary>
    /// The launch executable a wired entry names, whichever shape the harness uses: a
    /// <c>command</c> string beside <c>args</c>, or one array holding the executable and its
    /// arguments together (opencode). Read from the parsed document so a platform's path separators
    /// are compared by value rather than through their JSON escaping (R5.11).
    /// </summary>
    private static string ExecutableOf(JsonElement entry)
    {
        JsonElement command = entry.GetProperty("command");

        return command.ValueKind == JsonValueKind.Array
            ? command[0].GetString() ?? string.Empty
            : command.GetString() ?? string.Empty;
    }

    /// <summary>
    /// A repository-relative path as a path on this platform, so a fixture names a file the same way
    /// the design and the diagnostics do.
    /// </summary>
    private string InRepo(string relativePath) =>
        Path.Combine(Repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
