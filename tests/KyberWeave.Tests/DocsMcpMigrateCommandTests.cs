using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Mcp;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>docs mcp-migrate</c> surface: which old MCP server keys are renamed in an existing
/// install, which are only reported, which are refused, and what each run leaves on disk.
/// </summary>
/// <remarks>
/// <para>
/// Every case builds a repository tree in a temp folder, drives the command directly, and reads the
/// result back from disk. What is asserted is the observable outcome: the bytes that changed, the bytes
/// that did not, the receipt's records, the exit code, and the rule id the operator is shown.
/// </para>
/// <para>
/// A launch is proven by the file name of its first argument, never by a key alone. The fixtures put a
/// fake binary in a temp directory that is never on <c>PATH</c>, and declare it by absolute path, so the
/// suite never mutates the process environment and never depends on what the developer has installed.
/// </para>
/// <para>
/// The user-scope file the command must never touch is simulated by a fake home directory holding a
/// <c>.claude.json</c>. The command takes its repository from the path it is given and has no seam to
/// point it at this home, so that case pins that the file is left alone rather than proving the
/// command was denied a look at it.
/// </para>
/// </remarks>
public sealed class DocsMcpMigrateCommandTests : IDisposable
{
    /// <summary>The old key a first-party server was written under.</summary>
    private const string OldKey = "kyber-weave";

    /// <summary>The new key the shipped server is written under.</summary>
    private const string NewKey = "kw-docs";

    /// <summary>A proven old key is present; the remedy is the migrate command.</summary>
    private const string WarningCode = "KW-MCP-WIRE-007";

    /// <summary>An old key is present but not provably ours, so it is left alone.</summary>
    private const string InfoCode = "KW-MCP-WIRE-008";

    /// <summary>A target was refused and no byte was written to it.</summary>
    private const string ErrorCode = "KW-MCP-WIRE-009";

    /// <summary>The "no target" spelling: the command is a preview and writes nothing.</summary>
    private const string NoTarget = "";

    private const string ClaudeCode = "claude-code";
    private const string Factory = "factory";
    private const string OpenCode = "opencode";
    private const string Codex = "codex";
    private const string ZCode = "zcode";
    private const string Copilot = "copilot-coding-agent";
    private const string VsCode = "vscode";
    private const string Declaration = "declaration";

    private const string ClaudeFile = ".mcp.json";
    private const string ClaudeKey = "mcpServers";
    private const string FactoryFile = ".factory/mcp.json";
    private const string OpenCodeFile = "opencode.json";
    private const string CodexFile = ".codex/config.toml";
    private const string ZCodeFile = "zcode.json";
    private const string VsCodeFile = ".vscode/mcp.json";

    private const string DeclarationFile = ".kyber-weave/kyber-weave.yml";

    private readonly TempDirectory _root = new();

    public DocsMcpMigrateCommandTests()
    {
        Directory.CreateDirectory(Repo);
        Directory.CreateDirectory(Bin);
        Directory.CreateDirectory(FakeHome);
        McpWiringFixture.WriteExecutable(Bin, "kyber-weave-mcp");
        McpWiringFixture.WriteExecutable(Bin, "kyber-weave-arbiter");
        McpWiringFixture.WriteExecutable(Bin, "kyberdash");
    }

    public void Dispose() => _root.Dispose();

    private string Repo => Path.Combine(_root.Path, "repo");

    /// <summary>Where the fake launch binaries live: never on <c>PATH</c>, always declared by path.</summary>
    private string Bin => Path.Combine(_root.Path, "bin");

    /// <summary>A stand-in for the user's home directory, holding a user-scope configuration file.</summary>
    private string FakeHome => Path.Combine(_root.Path, "home");

    /// <summary>The shipped server's binary, by absolute path.</summary>
    private string ShippedBinary => Path.Combine(Bin, "kyber-weave-mcp");

    /// <summary>The receipt the wire repair keeps, at the path the design fixes.</summary>
    private string ReceiptPath => Path.Combine(Repo, ".kyber-weave", "receipts", "mcp-wiring.json");

    // ----------------------------------------------------------------------------------------
    // The preview and the rename table
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// With no target the command previews: it lists every old key it found with its proof result and
    /// the writable targets, writes not one byte, creates no receipt, and exits 0.
    /// </summary>
    [Fact]
    public void ANoTargetPreviewWritesNothingAndListsEveryOldKeyWithItsProof()
    {
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(NoTarget);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.False(File.Exists(ReceiptPath), "a preview must not write a receipt");
        Assert.Contains(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(ClaudeFile, execution.Output, StringComparison.Ordinal);
        Assert.Contains(ClaudeCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every run prints the rename table and a pointer to the user-scope files, because the command
    /// cannot see those and an operator must be told so.
    /// </summary>
    [Fact]
    public void EveryRunPrintsTheRenameTableAndTheUserScopePointer()
    {
        CapturedConsoleExecution<int> preview = Migrate(NoTarget);

        Assert.Equal(0, preview.Result);
        Assert.Contains("kyber-weave", preview.Output, StringComparison.Ordinal);
        Assert.Contains("kw-docs", preview.Output, StringComparison.Ordinal);
        Assert.Contains("kyberdash", preview.Output, StringComparison.Ordinal);
        Assert.Contains("kw-dash", preview.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", preview.Output, StringComparison.Ordinal);
        Assert.Contains("kw-arbiter", preview.Output, StringComparison.Ordinal);
        Assert.Contains("user", preview.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A dry run computes the same report a named run would, prints the rename, and writes nothing: no
    /// bytes, no receipt. It exits what the real run exits.
    /// </summary>
    [Fact]
    public void ADryRunPrintsTheRenameAndWritesNoBytesAndNoReceipt()
    {
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> dryRun = Migrate(ClaudeCode, dryRun: true);

        Assert.Equal(0, dryRun.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.False(File.Exists(ReceiptPath), "a dry run writes no receipt, not even one");
        Assert.Contains("renamed", dryRun.Output, StringComparison.OrdinalIgnoreCase);

        CapturedConsoleExecution<int> real = Migrate(ClaudeCode);

        Assert.Equal(real.Result, dryRun.Result);
        Assert.NotEqual(before, File.ReadAllBytes(InRepo(ClaudeFile)));
    }

    // ----------------------------------------------------------------------------------------
    // The seven state rows, per file and per old key
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A file with no old key is left alone and reports nothing about migration.
    /// </summary>
    [Fact]
    public void AFileWithNoOldKeyIsLeftAloneAndReportsNothing()
    {
        WriteClaudeEntry(NewKey, ShippedBinary, "--repo-root", ".");
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.DoesNotContain(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A proven old key with no new key beside it is rewritten to the new key, the bytes of the key
    /// changing and nothing else, and the run reports it as renamed.
    /// </summary>
    [Fact]
    public void AProvenOldKeyWithNoNewKeyBesideItIsRenamedAndReportedAsRenamed()
    {
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");
        string before = File.ReadAllText(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(
            before.Replace($"\"{OldKey}\"", $"\"{NewKey}\"", StringComparison.Ordinal),
            File.ReadAllText(InRepo(ClaudeFile)));
        Assert.Contains("renamed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WarningCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// An old key and a new key present with the same launch leaves one entry: the old member is
    /// removed byte-exact, and the run says it removed a duplicate.
    /// </summary>
    [Fact]
    public void ADuplicateOldKeyWithTheSameLaunchIsRemovedAndReportedAsRemovedDuplicate()
    {
        WriteClaudeTwoEntries(
            (OldKey, ShippedBinary, ["--repo-root", "."]),
            (NewKey, ShippedBinary, ["--repo-root", "."]));
        string before = File.ReadAllText(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        string after = File.ReadAllText(InRepo(ClaudeFile));
        Assert.DoesNotContain($"\"{OldKey}\"", after, StringComparison.Ordinal);
        Assert.Contains($"\"{NewKey}\"", after, StringComparison.Ordinal);
        Assert.Contains("removed duplicate", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(after.Length < before.Length, "removing the duplicate must shrink the file");
    }

    /// <summary>
    /// An old key and a new key present with different launches is refused: the file keeps every byte,
    /// both launches are shown, and the run exits 1 with the rule id.
    /// </summary>
    [Fact]
    public void ADuplicateOldKeyWithADifferentLaunchIsRefusedWithZeroBytesAndShowsBoth()
    {
        WriteClaudeTwoEntries(
            (OldKey, ShippedBinary, ["--repo-root", "."]),
            (NewKey, Path.Combine(Bin, "kyber-weave-arbiter"), ["serve"]));
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(1, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.Contains(ErrorCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-mcp", execution.Output, StringComparison.Ordinal);
        Assert.Contains("kyber-weave-arbiter", execution.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(ReceiptPath), "a refusal writes no receipt");
    }

    /// <summary>
    /// An old key whose launch is not provably ours is never touched. It is reported as info with the
    /// manual edit, and the run is not a failure.
    /// </summary>
    [Fact]
    public void AnOldKeyWhoseLaunchIsNotProvenIsNeverTouchedAndGetsTheManualEdit()
    {
        WriteClaudeEntry(OldKey, "npx", "kyber-weave-mcp");
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains(NewKey, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(WarningCode, execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file whose old key is a symbolic link is refused before any read, with zero bytes written and
    /// the rule id, and the file the link points at is untouched.
    /// </summary>
    [Fact]
    public void ASymlinkedTargetIsRefusedBeforeAnyReadAndExitsOne()
    {
        string elsewhere = Path.Combine(_root.Path, "elsewhere.json");
        File.WriteAllText(elsewhere, StdioDocument(ClaudeKey, OldKey, ShippedBinary, "--repo-root", "."));
        Directory.CreateDirectory(Repo);
        McpWiringFixture.CreateSymlink(InRepo(ClaudeFile), elsewhere);
        byte[] before = File.ReadAllBytes(elsewhere);

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(1, execution.Result);
        Assert.Contains(ErrorCode, execution.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(elsewhere));
    }

    /// <summary>
    /// A file that does not parse, or whose server table is a foreign shape, is refused with zero bytes
    /// written. A repair that guessed at the shape would rewrite something it does not understand.
    /// </summary>
    [Theory]
    [InlineData("{ \"mcpServers\": { \"kyber-weave\": ", "malformed")]
    [InlineData("{ \"mcpServers\": [ \"kyber-weave\" ] }", "foreign shape")]
    public void AMalformedOrForeignShapedTargetIsRefusedWithZeroBytes(string content, string shape)
    {
        McpWiringFixture.WriteText(Repo, ClaudeFile, content);
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.True(execution.Result == 1, $"a {shape} target must be refused. Output:{Environment.NewLine}{execution.Output}");
        Assert.Contains(ErrorCode, execution.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
    }

    /// <summary>
    /// A snippet-only harness is never written. The preview reports its old key as info and prints the
    /// snippet the operator pastes in, and the configuration file keeps every byte.
    /// </summary>
    [Fact]
    public void ACodexOldKeyIsReportedWithTheSnippetAndNeverWritten()
    {
        McpWiringFixture.WriteText(
            Repo,
            CodexFile,
            "[mcp_servers.kyber-weave]\n"
            + $"command = \"{ShippedBinary}\"\n"
            + "args = [\"--repo-root\", \".\"]\n");
        byte[] before = File.ReadAllBytes(InRepo(CodexFile));

        CapturedConsoleExecution<int> execution = Migrate(NoTarget);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(CodexFile)));
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains($"[mcp_servers.{NewKey}]", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// ZCode's configuration holds a proven old key under <c>mcp.servers</c>. The command reports it with
    /// the new key, in both the preview and a named run, and writes no byte to the file.
    /// </summary>
    [Fact]
    public void AZCodeOldKeyIsReportedWithTheNewKeyInPreviewAndNamedRunAndNeverWritten()
    {
        McpWiringFixture.WriteText(
            Repo,
            ZCodeFile,
            new JsonObject
            {
                ["mcp"] = new JsonObject
                {
                    ["servers"] = new JsonObject { [OldKey] = Entry(ShippedBinary, "--repo-root", ".") }
                }
            }.ToJsonString());
        byte[] before = File.ReadAllBytes(InRepo(ZCodeFile));

        CapturedConsoleExecution<int> preview = Migrate(NoTarget);
        CapturedConsoleExecution<int> named = Migrate(ZCode);

        Assert.Equal(0, preview.Result);
        Assert.Equal(0, named.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ZCodeFile)));
        Assert.Contains(InfoCode, preview.Output, StringComparison.Ordinal);
        Assert.Contains(InfoCode, named.Output, StringComparison.Ordinal);
        Assert.Contains(NewKey, named.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Named as a target, Codex is still report-only: exit 0, the file keeps every byte, and the
    /// snippet with the new key is printed for the operator to paste.
    /// </summary>
    [Fact]
    public void ACodexNamedTargetIsReportOnlyExitsZeroAndPrintsTheSnippet()
    {
        McpWiringFixture.WriteText(
            Repo,
            CodexFile,
            "[mcp_servers.kyber-weave]\n"
            + $"command = \"{ShippedBinary}\"\n"
            + "args = [\"--repo-root\", \".\"]\n");
        byte[] before = File.ReadAllBytes(InRepo(CodexFile));

        CapturedConsoleExecution<int> execution = Migrate(Codex);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(CodexFile)));
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains($"[mcp_servers.{NewKey}]", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Copilot coding agent keeps its MCP configuration in repository settings, which the command
    /// cannot reach. A named run exits 0, creates no file or directory, and says the entry lives in
    /// repository settings.
    /// </summary>
    [Fact]
    public void ACopilotCodingAgentTargetIsReportOnlyWritesNoFileAndNamesRepositorySettings()
    {
        WriteDeclaration();

        CapturedConsoleExecution<int> execution = Migrate(Copilot);

        Assert.Equal(0, execution.Result);
        Assert.False(Directory.Exists(InRepo(".github")), "a report-only target must create no directory");
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.Contains("repository settings", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removing a duplicate old member leaves every other byte exactly as it was. The member is removed
    /// as its whole line, wherever it sits: first, middle, or last in a file with a byte-order mark,
    /// a comment and another server. A re-serialising implementation would reorder or re-space these
    /// bytes and fail here.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RemovingTheDuplicateOldMemberLeavesEveryOtherByteExactlyAsItWas(int oldPosition)
    {
        string program = System.Text.Json.JsonSerializer.Serialize(ShippedBinary);
        string oldLine = $"    \"{OldKey}\": {{ \"type\": \"stdio\", \"command\": {program}, \"args\": [\"--repo-root\", \".\"] }},\n";
        string newLine = $"    \"{NewKey}\": {{ \"type\": \"stdio\", \"command\": {program}, \"args\": [\"--repo-root\", \".\"] }},\n";
        const string otherLine = "    \"other\": { \"command\": \"other-mcp\" },\n";
        const string header = "﻿// the team's servers; keep this comment\n{\n  // claude code reads this table\n  \"mcpServers\": {\n";
        const string footer = "  }\n}\n";

        List<string> entries = [otherLine, newLine];
        entries.Insert(oldPosition, oldLine);

        string original = header + string.Concat(entries) + footer;
        string expected = header + string.Concat(entries.Where(line => line != oldLine)) + footer;
        McpWiringFixture.WriteBytes(Repo, ClaudeFile, Encoding.UTF8.GetBytes(original));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.Contains("removed duplicate", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A difference in the harness root token alone is not a difference in the launch. The old entry
    /// says <c>.</c> and the new one says <c>${workspaceFolder}</c>, which VS Code substitutes for the
    /// same directory, so the old member is a duplicate and is removed byte-exact.
    /// </summary>
    [Fact]
    public void AHarnessRootTokenDifferenceAloneCountsAsTheSameLaunchAndTheOldMemberIsRemoved()
    {
        string program = System.Text.Json.JsonSerializer.Serialize(ShippedBinary);
        string oldLine = $"    \"{OldKey}\": {{ \"type\": \"stdio\", \"command\": {program}, \"args\": [\"--repo-root\", \".\"] }},\n";
        string newLine = $"    \"{NewKey}\": {{ \"type\": \"stdio\", \"command\": {program}, \"args\": [\"--repo-root\", \"${{workspaceFolder}}\"] }},\n";
        const string otherLine = "    \"other\": { \"command\": \"other-mcp\" },\n";
        const string header = "{\n  \"servers\": {\n";
        const string footer = "  }\n}\n";

        string original = header + otherLine + oldLine + newLine + footer;
        string expected = header + otherLine + newLine + footer;
        McpWiringFixture.WriteBytes(Repo, VsCodeFile, Encoding.UTF8.GetBytes(original));

        CapturedConsoleExecution<int> execution = Migrate(VsCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(Encoding.UTF8.GetBytes(expected), File.ReadAllBytes(InRepo(VsCodeFile)));
        Assert.Contains("removed duplicate", execution.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A named harness run also renames the declared server, so the host declaration and the harness file
    /// agree afterwards. The declaration's comments stay, and the file reloads under the new name.
    /// </summary>
    [Fact]
    public void ANamedHarnessTargetAlsoRenamesTheDeclarationKeyOnItsOwnLine()
    {
        WriteDeclarationWithComments();
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");
        string before = File.ReadAllText(InRepo(DeclarationFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(
            before.Replace($"    {OldKey}:", $"    {NewKey}:", StringComparison.Ordinal),
            File.ReadAllText(InRepo(DeclarationFile)));
        Assert.Contains(NewKey, LoadConfig().ServerNames, StringComparer.Ordinal);
    }

    /// <summary>
    /// A dry run of a named harness target reports the declaration rename but writes neither the
    /// declaration nor the harness file.
    /// </summary>
    [Fact]
    public void ADryRunOfANamedHarnessTargetLeavesTheDeclarationKeyUnchanged()
    {
        WriteDeclarationWithComments();
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");
        byte[] declarationBefore = File.ReadAllBytes(InRepo(DeclarationFile));
        byte[] claudeBefore = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode, dryRun: true);

        Assert.Equal(0, execution.Result);
        Assert.Equal(declarationBefore, File.ReadAllBytes(InRepo(DeclarationFile)));
        Assert.Equal(claudeBefore, File.ReadAllBytes(InRepo(ClaudeFile)));
    }

    /// <summary>
    /// A declared server whose launch is <c>npx kyber-weave-mcp</c> and whose provenance is third-party is
    /// not ours. The declaration keeps every byte, the run reports it as info, and it exits 0.
    /// </summary>
    [Fact]
    public void AThirdPartyDeclaredServerLaunchedThroughNpxIsUntouchedAndReportedAsInfo()
    {
        WriteFile(
            DeclarationFile,
            "mcp:\n"
            + "  required-servers:\n"
            + $"    {OldKey}:\n"
            + "      command: [npx, kyber-weave-mcp]\n"
            + "      provenance: third-party\n"
            + "      level: required\n");
        byte[] before = File.ReadAllBytes(InRepo(DeclarationFile));

        CapturedConsoleExecution<int> execution = Migrate(Declaration);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(DeclarationFile)));
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    // The proof rule: which launches count as ours
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// Each old key is proven only by its own shipped file name, with the next argument the rule names.
    /// A proven entry is renamed; an unproven one is left alone.
    /// </summary>
    [Theory]
    [InlineData("kyberdash", "kw-dash", "kyberdash", "mcp", true)]
    [InlineData("kyberdash", "kw-dash", "kyberdash", "web", false)]
    [InlineData("kyber-weave-arbiter", "kw-arbiter", "kyber-weave-arbiter", "serve", true)]
    [InlineData("kyber-weave-arbiter", "kw-arbiter", "kyber-weave-arbiter", "hook", false)]
    [InlineData("kyber-weave", "kw-docs", "kyber-weave-mcp", "", true)]
    public void TheProofRuleRenamesOnlyLaunchesThatNameTheShippedBinaryAndItsSubcommand(
        string oldKey,
        string newKey,
        string binary,
        string nextArgument,
        bool proven)
    {
        string program = Path.Combine(Bin, binary);
        string[] arguments = nextArgument.Length == 0 ? ["--repo-root", "."] : [nextArgument];
        WriteClaudeEntry(oldKey, program, arguments);
        string before = File.ReadAllText(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        string after = File.ReadAllText(InRepo(ClaudeFile));
        if (proven)
        {
            Assert.Equal(before.Replace($"\"{oldKey}\"", $"\"{newKey}\"", StringComparison.Ordinal), after);
        }
        else
        {
            Assert.Equal(before, after);
            Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A launch whose directory is a harness variable is still ours when its file name is the shipped
    /// one: the file name comparison ignores the directory, as the shipped-binary rule does.
    /// </summary>
    [Fact]
    public void AUserHomeLaunchWithTheShippedFileNameIsProven()
    {
        WriteClaudeEntry(OldKey, "${userHome}/.local/bin/kyber-weave-mcp", "--repo-root", ".");
        string before = File.ReadAllText(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(
            before.Replace($"\"{OldKey}\"", $"\"{NewKey}\"", StringComparison.Ordinal),
            File.ReadAllText(InRepo(ClaudeFile)));
    }

    /// <summary>
    /// A Windows executable suffix on the shipped file name is the same program, and is proven.
    /// </summary>
    [Fact]
    public void AnExecutableSuffixOnTheShippedFileNameIsProven()
    {
        McpWiringFixture.WriteExecutable(Bin, "kyber-weave-mcp.exe");
        WriteClaudeEntry(OldKey, Path.Combine(Bin, "kyber-weave-mcp.exe"), "--repo-root", ".");
        string before = File.ReadAllText(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(
            before.Replace($"\"{OldKey}\"", $"\"{NewKey}\"", StringComparison.Ordinal),
            File.ReadAllText(InRepo(ClaudeFile)));
    }

    /// <summary>
    /// A wrapper that runs the shipped binary through another program is unproven. The first argument
    /// is <c>npx</c>, so the entry is reported and left alone rather than assumed to be ours.
    /// </summary>
    [Fact]
    public void AWrapperLaunchSuchAsNpxIsUnprovenAndLeftAlone()
    {
        WriteClaudeEntry(OldKey, "npx", "kyber-weave-mcp");
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    // Byte preservation and the single-array harness
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A file with a byte-order mark, comments and trailing commas changes only the bytes of the old key.
    /// Every other byte, including the mark and the comments, is identical after the run.
    /// </summary>
    [Fact]
    public void RenamingTheKeyLeavesEveryOtherByteOfAFileWithBomCommentsAndTrailingCommas()
    {
        string text =
            "﻿// the team's servers; keep this comment\n"
            + "{\n"
            + "  // claude code reads this table\n"
            + "  \"mcpServers\": {\n"
            + "    \"other\": { \"command\": \"other-mcp\", },\n"
            + $"    \"{OldKey}\": {{ \"type\": \"stdio\", \"command\": \"{ShippedBinary}\", \"args\": [\"--repo-root\", \".\"], }},\n"
            + "  },\n"
            + "}\n";
        byte[] before = Encoding.UTF8.GetBytes(text);
        McpWiringFixture.WriteBytes(Repo, ClaudeFile, before);

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        byte[] expected = Encoding.UTF8.GetBytes(
            text.Replace($"\"{OldKey}\"", $"\"{NewKey}\"", StringComparison.Ordinal));
        Assert.Equal(expected, File.ReadAllBytes(InRepo(ClaudeFile)));
    }

    /// <summary>
    /// opencode holds the executable and its arguments in one array. The first element is what proves the
    /// launch, and the rename changes only the key.
    /// </summary>
    [Fact]
    public void AnOpencodeSingleArrayLaunchIsProvenByItsFirstElementAndRenamedInPlace()
    {
        string text =
            "{\n"
            + "  \"mcp\": {\n"
            + $"    \"{OldKey}\": {{ \"type\": \"local\", \"command\": [\"{ShippedBinary}\", \"--repo-root\", \".\"] }}\n"
            + "  }\n"
            + "}\n";
        McpWiringFixture.WriteText(Repo, OpenCodeFile, text);

        CapturedConsoleExecution<int> execution = Migrate(OpenCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(
            text.Replace($"\"{OldKey}\"", $"\"{NewKey}\"", StringComparison.Ordinal),
            File.ReadAllText(InRepo(OpenCodeFile)));
    }

    // ----------------------------------------------------------------------------------------
    // The receipt, and mcp-unwire
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A file wired by this tool and then migrated has its receipt rebased, not replaced. Removal then
    /// restores the bytes from before the wire, and a file the wire created is deleted.
    /// </summary>
    [Fact]
    public void ARebasedReceiptLetsUnwireRestoreThePreWireBytesOfACreatedFile()
    {
        WriteDeclaration();

        Assert.Equal(0, Wire(Factory).Result);
        Assert.True(File.Exists(InRepo(FactoryFile)), "the wire must have created the file first");

        CapturedConsoleExecution<int> migrate = Migrate(Factory);
        Assert.Equal(0, migrate.Result);

        CapturedConsoleExecution<int> unwire = Unwire(Factory);

        Assert.Equal(0, unwire.Result);
        Assert.False(File.Exists(InRepo(FactoryFile)), "a file the wire created is deleted on removal");
        Assert.Null(McpWiringReceipt.Load(Repo).Find(FactoryFile));
    }

    /// <summary>
    /// The rebased receipt also restores an inserted file's pre-wire bytes, which is the original file
    /// without the server the wire added and without the rename the migrate made.
    /// </summary>
    [Fact]
    public void ARebasedReceiptLetsUnwireRestoreThePreWireBytesOfAnInsertedFile()
    {
        WriteDeclaration();
        byte[] original = Encoding.UTF8.GetBytes("""{"mcpServers": {"other": {"command": "other-mcp"}}}""");
        McpWiringFixture.WriteBytes(Repo, FactoryFile, original);

        Assert.Equal(0, Wire(Factory).Result);
        Assert.Equal(0, Migrate(Factory).Result);

        CapturedConsoleExecution<int> unwire = Unwire(Factory);

        Assert.Equal(0, unwire.Result);
        Assert.Equal(original, File.ReadAllBytes(InRepo(FactoryFile)));
    }

    /// <summary>
    /// A migrate with no prior receipt records an inserted-mode entry whose pre-write bytes are the file as
    /// it was before the migrate. Removal restores exactly those bytes.
    /// </summary>
    [Fact]
    public void AMigrateWithNoPriorRecordThenUnwireRestoresThePreMigrateBytes()
    {
        WriteDeclaration();
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");
        byte[] preMigrate = File.ReadAllBytes(InRepo(ClaudeFile));
        Assert.False(File.Exists(ReceiptPath));

        Assert.Equal(0, Migrate(ClaudeCode).Result);
        Assert.NotNull(McpWiringReceipt.Load(Repo).Find(ClaudeFile));

        CapturedConsoleExecution<int> unwire = Unwire(ClaudeCode);

        Assert.Equal(0, unwire.Result);
        Assert.Equal(preMigrate, File.ReadAllBytes(InRepo(ClaudeFile)));
    }

    // ----------------------------------------------------------------------------------------
    // The host declaration
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// The declaration's server name is edited on its one line, every comment stays, and the file reloads
    /// with the new name in place of the old one.
    /// </summary>
    [Fact]
    public void TheDeclarationKeyIsEditedOnItsOwnLineKeepingCommentsAndReloadsAsKwDocs()
    {
        WriteDeclarationWithComments();
        string before = File.ReadAllText(InRepo(DeclarationFile));

        CapturedConsoleExecution<int> execution = Migrate(Declaration);

        Assert.Equal(0, execution.Result);
        string after = File.ReadAllText(InRepo(DeclarationFile));
        Assert.Equal(before.Replace($"    {OldKey}:", $"    {NewKey}:", StringComparison.Ordinal), after);
        Assert.Contains("# Host declaration: keep this comment.", after, StringComparison.Ordinal);
        Assert.Contains("# The shipped server.", after, StringComparison.Ordinal);

        LoadResult reloaded = LoadConfig();
        Assert.True(reloaded.Success, $"the edited declaration must reload: {reloaded.Error}");
        Assert.True(
            reloaded.ServerNames.Contains(NewKey, StringComparer.Ordinal),
            $"expected '{NewKey}' in the reloaded declaration, found: {string.Join(", ", reloaded.ServerNames)}");
    }

    /// <summary>
    /// A declaration written in flow style is refused, with the edit the operator should make, and the
    /// file keeps every byte. A text edit of a flow mapping cannot be proven to keep the rest intact.
    /// </summary>
    [Fact]
    public void AFlowStyleDeclarationIsRefusedWithTheEditToMakeAndZeroBytes()
    {
        WriteFile(
            DeclarationFile,
            "mcp:\n"
            + "  required-servers: {"
            + $"{OldKey}: {{command: ['{ShippedBinary}', --repo-root, .], provenance: first-party, level: required}}"
            + "}\n");
        byte[] before = File.ReadAllBytes(InRepo(DeclarationFile));

        CapturedConsoleExecution<int> execution = Migrate(Declaration);

        Assert.Equal(1, execution.Result);
        Assert.Contains(ErrorCode, execution.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(InRepo(DeclarationFile)));
    }

    // ----------------------------------------------------------------------------------------
    // Idempotence and user scope
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A second run after a rename reports nothing to do, exits 0, and writes no byte.
    /// </summary>
    [Fact]
    public void ASecondRunReportsNothingToDoAndWritesNoByte()
    {
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");

        Assert.Equal(0, Migrate(ClaudeCode).Result);
        byte[] afterFirst = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> second = Migrate(ClaudeCode);

        Assert.Equal(0, second.Result);
        Assert.Equal(afterFirst, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.Contains("nothing to do", second.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A user-scope configuration file holding a <c>kyberdash</c> entry keeps its bytes across a preview and
    /// a named run, and its content never reaches the output. The command has no seam that points it at this
    /// fake home, so this pins the observable outcome only. It does not prove the command never reads the file.
    /// </summary>
    [Fact]
    public void AUserScopeClaudeJsonStaysByteIdenticalAndItsContentNeverAppears()
    {
        const string marker = "home-secret-marker";
        string userFile = Path.Combine(FakeHome, ".claude.json");
        File.WriteAllText(
            userFile,
            "{\"mcpServers\": {\"kyberdash\": {\"command\": \"/" + marker + "/kyberdash\", \"args\": [\"mcp\"]}}}");
        byte[] before = File.ReadAllBytes(userFile);
        WriteClaudeEntry(OldKey, ShippedBinary, "--repo-root", ".");

        CapturedConsoleExecution<int> preview = Migrate(NoTarget);
        CapturedConsoleExecution<int> named = Migrate(ClaudeCode);

        Assert.Equal(before, File.ReadAllBytes(userFile));
        Assert.DoesNotContain(marker, preview.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, named.Output, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    // Fixtures
    // ----------------------------------------------------------------------------------------

    /// <summary>Runs the migrate verb against the fixture repository.</summary>
    private CapturedConsoleExecution<int> Migrate(string targets, bool dryRun = false) =>
        ProcessConsoleCapture.Run(() => new DocsMcpMigrateCommand().Execute(
            null!,
            new DocsMcpMigrateSettings { Path = Repo, Target = targets, DryRun = dryRun }));

    /// <summary>Runs the wire verb, to produce a receipt the migrate verb must rebase.</summary>
    private CapturedConsoleExecution<int> Wire(string targets) =>
        ProcessConsoleCapture.Run(() => new DocsMcpWireCommand().Execute(
            null!,
            new DocsMcpWireSettings { Path = Repo, Target = targets }));

    /// <summary>Runs the removal verb against the receipt the wire and migrate verbs wrote.</summary>
    private CapturedConsoleExecution<int> Unwire(string targets) =>
        ProcessConsoleCapture.Run(() => new DocsMcpUnwireCommand().Execute(
            null!,
            new DocsMcpUnwireSettings { Path = Repo, Target = targets }));

    /// <summary>The declaration <c>docs init</c> scaffolds, naming the shipped binary by absolute path.</summary>
    private void WriteDeclaration() =>
        WriteFile(
            DeclarationFile,
            $$"""
            mcp:
              required-servers:
                {{OldKey}}:
                  command: ['{{ShippedBinary}}', --repo-root, .]
                  provenance: first-party
                  level: required
            """);

    private void WriteDeclarationWithComments() =>
        WriteFile(
            DeclarationFile,
            $$"""
            # Host declaration: keep this comment.
            mcp:
              required-servers:
                # The shipped server.
                {{OldKey}}:
                  command: ['{{ShippedBinary}}', --repo-root, .]
                  provenance: first-party
                  level: required
            """);

    /// <summary>Writes one Claude Code entry under the old or new key, with the given launch.</summary>
    private void WriteClaudeEntry(string key, string program, params string[] arguments) =>
        McpWiringFixture.WriteText(Repo, ClaudeFile, StdioDocument(ClaudeKey, key, program, arguments));

    /// <summary>Writes a Claude Code file holding two entries, each with its own launch.</summary>
    private void WriteClaudeTwoEntries(
        (string Key, string Program, string[] Arguments) first,
        (string Key, string Program, string[] Arguments) second)
    {
        JsonObject servers = new()
        {
            [first.Key] = Entry(first.Program, first.Arguments),
            [second.Key] = Entry(second.Program, second.Arguments)
        };
        McpWiringFixture.WriteText(Repo, ClaudeFile, new JsonObject { [ClaudeKey] = servers }.ToJsonString());
    }

    private static string StdioDocument(string rootKey, string key, string program, params string[] arguments) =>
        new JsonObject { [rootKey] = new JsonObject { [key] = Entry(program, arguments) } }.ToJsonString();

    private static JsonObject Entry(string program, params string[] arguments)
    {
        JsonArray args = [];
        foreach (string argument in arguments)
            args.Add(argument);

        return new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = program,
            ["args"] = args
        };
    }

    private void WriteFile(string relativePath, string content) =>
        McpWiringFixture.WriteText(Repo, relativePath, content);

    private string InRepo(string relativePath) =>
        Path.Combine(Repo, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The outcome of loading the host declaration back through the configuration loader.</summary>
    private sealed record LoadResult(bool Success, string? Error, IReadOnlyList<string> ServerNames);

    private LoadResult LoadConfig()
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(Repo);
        IReadOnlyList<string> names = result.Config?.Mcp is { } mcp
            ? [.. mcp.RequiredServers.Keys]
            : [];

        return new LoadResult(result.Success, result.Error, names);
    }
}
