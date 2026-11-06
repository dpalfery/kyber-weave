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
/// <c>.claude.json</c>. The command takes its repository from the path it is given, and the case that
/// runs against the repository pins that the file is left alone rather than proving the command was
/// denied a look at it. The case that supplies the fake home as the root is the other half: there the
/// home itself is the operator-named tree, so the refusal is the only outcome the fixtures can see.
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
    private const string Cursor = "cursor";
    private const string Factory = "factory";
    private const string OpenCode = "opencode";
    private const string Codex = "codex";
    private const string ZCode = "zcode";
    private const string Copilot = "copilot-coding-agent";
    private const string VsCode = "vscode";
    private const string Declaration = "declaration";

    private const string ClaudeFile = ".mcp.json";
    private const string ClaudeKey = "mcpServers";
    private const string CursorFile = ".cursor/mcp.json";
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

    /// <summary>
    /// A stand-in for the user's home directory, holding a user-scope configuration file.
    /// </summary>
    /// <remarks>
    /// The directory is deliberately not spelled <c>home</c>. The command prints the root it was given
    /// on its first line, so a directory named <c>home</c> would satisfy "the refusal names the home
    /// directory" from the path alone, and the assertion would pass on a run that refused nothing.
    /// </remarks>
    private string FakeHome => Path.Combine(_root.Path, "user-profile");

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
    /// <remarks>
    /// The positive control at the end is the point of this test. "Nothing happened" is also what a
    /// command that does nothing at all produces, so the same run is repeated over a second harness
    /// file that does hold a proven old key: that one must be renamed. The silence about the first
    /// file is then the command having looked and found nothing, not the command being absent.
    /// </remarks>
    [Fact]
    public void AFileWithNoOldKeyIsLeftAloneAndReportsNothing()
    {
        WriteClaudeEntry(NewKey, ShippedBinary, "--repo-root", ".");
        WriteFile(FactoryFile, StdioDocument(ClaudeKey, OldKey, ShippedBinary, "--repo-root", "."));
        byte[] before = File.ReadAllBytes(InRepo(ClaudeFile));

        CapturedConsoleExecution<int> execution = Migrate(ClaudeCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ClaudeFile)));
        Assert.DoesNotContain(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorCode, execution.Output, StringComparison.Ordinal);

        // The control: a file that does hold a proven old key is renamed by the same command.
        CapturedConsoleExecution<int> control = Migrate(Factory);

        Assert.Equal(0, control.Result);
        string controlled = File.ReadAllText(InRepo(FactoryFile));
        Assert.Contains($"\"{NewKey}\"", controlled, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{OldKey}\"", controlled, StringComparison.Ordinal);
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

        // The manual edit is only actionable if it says which file to make it in.
        Assert.Contains(ClaudeFile, execution.Output, StringComparison.Ordinal);
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

        // A report-only target that did not say which file holds the entry leaves the operator to
        // search for it, in a harness whose configuration file is not one this tool writes.
        Assert.Contains(ZCodeFile, preview.Output, StringComparison.Ordinal);
        Assert.Contains(ZCodeFile, named.Output, StringComparison.Ordinal);
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

        // The snippet is the whole report for a file the tree does not hold: it has to carry the new key
        // the operator pastes into the settings page.
        Assert.Contains(NewKey, execution.Output, StringComparison.Ordinal);
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
    /// <remarks>
    /// The real run at the end is the control. It proves the dry run's silence is a held-back write and
    /// not a command that never had one to make.
    /// </remarks>
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

        // The control: the same run without --dry-run writes both.
        Assert.Equal(0, Migrate(ClaudeCode).Result);
        Assert.NotEqual(declarationBefore, File.ReadAllBytes(InRepo(DeclarationFile)));
        Assert.NotEqual(claudeBefore, File.ReadAllBytes(InRepo(ClaudeFile)));
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

        // Left alone, but told what to do: the notice names the declaration and the key to use instead.
        Assert.Contains(DeclarationFile, execution.Output, StringComparison.Ordinal);
        Assert.Contains(NewKey, execution.Output, StringComparison.Ordinal);
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

        // The rename really happened after the wire, so the receipt kept for this file is a rebased one
        // and not the original the wire wrote.
        string migrated = File.ReadAllText(InRepo(FactoryFile));
        Assert.Contains($"\"{NewKey}\"", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{OldKey}\"", migrated, StringComparison.Ordinal);

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

        CapturedConsoleExecution<int> migrate = Migrate(Factory);
        Assert.Equal(0, migrate.Result);

        string migrated = File.ReadAllText(InRepo(FactoryFile));
        Assert.Contains($"\"{NewKey}\"", migrated, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{OldKey}\"", migrated, StringComparison.Ordinal);

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

        // A refusal an operator cannot act on is only half a refusal: the edit to make is naming the new key.
        Assert.Contains(NewKey, execution.Output, StringComparison.Ordinal);
        Assert.Contains(DeclarationFile, execution.Output, StringComparison.Ordinal);
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

        Assert.Equal(0, preview.Result);
        Assert.Equal(0, named.Result);
        Assert.Equal(before, File.ReadAllBytes(userFile));
        Assert.DoesNotContain(marker, preview.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, named.Output, StringComparison.Ordinal);

        // The repository entry the named run was pointed at was renamed, so the user-scope file staying
        // identical is the command writing only where it was told to, not the command writing nothing.
        Assert.Contains($"\"{NewKey}\"", File.ReadAllText(InRepo(ClaudeFile)), StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    // What a report-only target reports: the file it read, and the proof it did not have
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A Codex table is reported as a leftover key and never written, and the notice keeps its
    /// qualifier whichever launch the table holds. The TOML reader reports table names and nothing
    /// else, so the body — <c>command</c> and <c>args</c> — is never read, and nothing here proves
    /// the entry is ours.
    /// </summary>
    /// <remarks>
    /// The two rows are the point. A table launching <c>npx some-other-server</c> is plainly not
    /// ours, and a table holding exactly the shipped launch looks exactly ours — and the report has
    /// to be the same report for both, because the evidence is the same for both: none. A notice
    /// that drops the qualifier claims a proof this command never obtained, and it tells an operator
    /// to rename an entry nobody read.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACodexTomlTableIsReportedAsALeftoverKeyAndNeverAsAProvenLaunch(bool shippedLaunch)
    {
        string body = shippedLaunch
            ? $"command = \"{ShippedBinary}\"\nargs = [\"--repo-root\", \".\"]\n"
            : "command = \"npx\"\nargs = [\"some-other-server\"]\n";
        McpWiringFixture.WriteText(Repo, CodexFile, $"[mcp_servers.{OldKey}]\n{body}");
        byte[] before = File.ReadAllBytes(InRepo(CodexFile));

        CapturedConsoleExecution<int> execution = Migrate(Codex);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(CodexFile)));
        Assert.Contains(InfoCode, execution.Output, StringComparison.Ordinal);

        // The unqualified remedy is the claim that the entry is provably ours, and a body nobody
        // read cannot earn it.
        Assert.DoesNotContain(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.True(
            execution.Output.Contains("if that", StringComparison.Ordinal),
            "a Codex table is reported without its launch being read, so the notice must keep the qualifier "
                + "('if that entry is Kyber-Weave's', 'if that table launches …'), never an unqualified rename. "
                + $"Output:{Environment.NewLine}{execution.Output}");
    }

    /// <summary>
    /// A report-only file that was read and holds no retired key is nothing to do: no line naming an
    /// old key, and no remedy for an entry the file does not hold.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is the fall-through that makes a clean file look like a leftover.
    /// "The loop above added nothing" is true both of a file holding no old key and of a harness with
    /// no file in the tree at all, and only the second has a snippet to hand out. Reporting a
    /// <c>KW-MCP-WIRE-008</c> for the first tells the operator to rename a key the file does not
    /// hold — and it is what a second run over a migrated tree would print instead of idempotence.
    /// </remarks>
    [Fact]
    public void AReportOnlyFileWithNoRetiredKeyIsNothingToDoAndGetsNoLeftoverKeyLine()
    {
        McpWiringFixture.WriteText(Repo, ZCodeFile, """{"mcp":{"servers":{}}}""");
        byte[] before = File.ReadAllBytes(InRepo(ZCodeFile));

        CapturedConsoleExecution<int> execution = Migrate(ZCode);

        Assert.Equal(0, execution.Result);
        Assert.Equal(before, File.ReadAllBytes(InRepo(ZCodeFile)));
        Assert.DoesNotContain(InfoCode, execution.Output, StringComparison.Ordinal);
        Assert.True(
            execution.Output.Contains("nothing to do", StringComparison.OrdinalIgnoreCase),
            "an empty server map holds no retired key, so the run has nothing to do. "
                + $"Output:{Environment.NewLine}{execution.Output}");
    }

    /// <summary>
    /// A report-only file this command cannot analyse is reported as that analysis problem, never as
    /// a leftover key.
    /// </summary>
    /// <remarks>
    /// "A key was left alone" is a statement about a key that was found in a readable file. This
    /// file's content was never read, so the same line says the opposite of what happened: the
    /// operator is pointed at a key to rename instead of at the document that needs fixing.
    /// </remarks>
    [Theory]
    [InlineData("{ \"mcp\": { \"servers\": ")]
    [InlineData("{ \"mcp\": { \"servers\": [ \"kyber-weave\" ] } }")]
    public void AReportOnlyFileThatCannotBeAnalysedReportsTheProblemAndNotALeftoverKey(string content)
    {
        McpWiringFixture.WriteText(Repo, ZCodeFile, content);
        byte[] before = File.ReadAllBytes(InRepo(ZCodeFile));

        CapturedConsoleExecution<int> execution = Migrate(ZCode);

        Assert.Equal(before, File.ReadAllBytes(InRepo(ZCodeFile)));
        Assert.True(
            execution.Output.Contains("cannot be migrated", StringComparison.Ordinal),
            "a file this command could not analyse has an analysis problem to report, and the report is the only "
                + $"place an operator can learn it. Output:{Environment.NewLine}{execution.Output}");
        Assert.DoesNotContain("was left alone", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A report-only target is idempotent: over a tree that already holds the new key and no old one,
    /// the first run and the second both report nothing to do, exit 0 and write no byte.
    /// </summary>
    /// <remarks>
    /// "A second run over a migrated tree reports nothing to do" is the bound the runbook states for
    /// this verb, and it was only covered for a writable target, where the write itself proves the
    /// first run happened. A report-only target has no write, so its idempotence is entirely the
    /// report's: a leftover-key line invented for a clean file would survive every run and send an
    /// operator to edit a file that is already correct.
    /// </remarks>
    [Fact]
    public void ASecondRunOfAReportOnlyTargetOverAMigratedTreeReportsNothingToDo()
    {
        McpWiringFixture.WriteText(
            Repo,
            CodexFile,
            $"[mcp_servers.{NewKey}]\n"
            + $"command = \"{ShippedBinary}\"\n"
            + "args = [\"--repo-root\", \".\"]\n");
        byte[] before = File.ReadAllBytes(InRepo(CodexFile));

        CapturedConsoleExecution<int> first = Migrate(Codex);
        CapturedConsoleExecution<int> second = Migrate(Codex);

        CapturedConsoleExecution<int>[] runs = [first, second];

        Assert.Equal(before, File.ReadAllBytes(InRepo(CodexFile)));
        foreach (CapturedConsoleExecution<int> run in runs)
        {
            Assert.Equal(0, run.Result);
            Assert.DoesNotContain(InfoCode, run.Output, StringComparison.Ordinal);
            Assert.True(
                run.Output.Contains("nothing to do", StringComparison.OrdinalIgnoreCase),
                $"a tree holding '{NewKey}' and no retired key has nothing to do, on every run. "
                    + $"Output:{Environment.NewLine}{run.Output}");
        }
    }

    // ----------------------------------------------------------------------------------------
    // What a write records: Written means the bytes reached the file
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// One run over one file holding one proven old key and one unproven one reports both, and marks
    /// only the proven row written: <c>Written</c> says whether bytes reached the file, so a
    /// <c>KW-MCP-WIRE-008</c> row — a key reported and left alone — is never written.
    /// </summary>
    /// <remarks>
    /// Both rules sit in one file because the flag is the file's, not the row's: the apply path used
    /// to mark every row of a file it wrote, which turns "a key was left alone" into a claim that
    /// the tool changed it. The record's own contract is the reason the distinction is worth a test
    /// — anything reading the report takes <c>Written</c> at its word.
    /// </remarks>
    [Fact]
    public void ARenamedRowIsWrittenAndALeftAloneRowInTheSameFileIsNot()
    {
        WriteClaudeTwoEntries(
            (OldKey, ShippedBinary, ["--repo-root", "."]),
            ("kyberdash", "npx", []));

        IReadOnlyList<McpRenameChange> changes = McpServerRenamer.Migrate(Repo, [ClaudeCode]);

        McpRenameChange renamed = Assert.Single(changes, change => change.OldKey == OldKey);
        Assert.Equal(McpRenameOutcome.Renamed, renamed.Outcome);
        Assert.True(renamed.Written, "the proven key was renamed in place, so its bytes reached the file");

        McpRenameChange leftAlone = Assert.Single(changes, change => change.OldKey == "kyberdash");
        Assert.Equal(InfoCode, leftAlone.Code);
        Assert.Equal(McpRenameOutcome.Reported, leftAlone.Outcome);
        Assert.False(
            leftAlone.Written,
            "a KW-MCP-WIRE-008 row is a key reported and left alone, so no byte of it reached the file");

        // The flag is not the only evidence: the file shows the rename really happened and the
        // unproven entry really survived, so "not written" describes a held-back edit and not a run
        // that did nothing.
        string after = File.ReadAllText(InRepo(ClaudeFile));
        Assert.Contains($"\"{NewKey}\"", after, StringComparison.Ordinal);
        Assert.Contains("\"kyberdash\"", after, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------
    // A root that is the operator's home directory
    // ----------------------------------------------------------------------------------------

    /// <summary>
    /// A root that is the operator's home directory is refused before a byte is read or written:
    /// exit 1, <c>KW-MCP-WIRE-009</c>, the user-scope file byte-identical, no receipt — and the
    /// refusal names the reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The catalog holds repository-relative paths, so every bound in this verb is containment under
    /// the root the operator named. Point that root at a home directory and the same relative path —
    /// <c>.cursor/mcp.json</c> — is the user-scope file ADR 0052 decision 2 says is never read or
    /// written, and the command's own header promises the same. Containment alone is not the
    /// guarantee either statement is written down as.
    /// </para>
    /// <para>
    /// The fixture is a fake home under the temp directory and never the real one: the case must not
    /// depend on what the developer's own <c>$HOME</c> holds, and the defect it pins would rewrite
    /// the developer's own Cursor configuration. The verb takes its root from its one path argument,
    /// so that is the only input this case supplies; a fix that resolves the operator's home through
    /// an injected reader — as <c>RepositoryDoctor.Run</c> does, defaulting to the process
    /// environment — is wired at the one <c>MigrateAt</c> call below, and none of these assertions
    /// change with it.
    /// </para>
    /// </remarks>
    [Fact]
    public void AHomeDirectoryRootIsRefusedAndLeavesTheUserScopeFileByteIdentical()
    {
        McpWiringFixture.WriteText(
            FakeHome,
            CursorFile,
            StdioDocument(ClaudeKey, OldKey, ShippedBinary, "--repo-root", "."));

        // A home directory is where a harness keeps its user-scope files, and this one holds the one
        // this suite already stands in for: the reason the root is refused at all.
        McpWiringFixture.WriteText(FakeHome, ".claude.json", """{"mcpServers": {}}""");

        byte[] before = File.ReadAllBytes(InHome(CursorFile));
        string receipt = Path.Combine(FakeHome, ".kyber-weave", "receipts", "mcp-wiring.json");

        CapturedConsoleExecution<int> execution = MigrateAt(FakeHome, Cursor);

        Assert.Equal(1, execution.Result);
        Assert.Contains(ErrorCode, execution.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(InHome(CursorFile)));
        Assert.False(File.Exists(receipt), "a refused root records nothing, so a write there could not be undone");
        Assert.DoesNotContain(WarningCode, execution.Output, StringComparison.Ordinal);
        Assert.True(
            execution.Output.Contains("home", StringComparison.OrdinalIgnoreCase),
            "the refusal must name the reason: the root is the operator's home directory, whose files this "
                + $"command never reads or writes. Output:{Environment.NewLine}{execution.Output}");
    }

    // ----------------------------------------------------------------------------------------
    // Fixtures
    // ----------------------------------------------------------------------------------------

    /// <summary>Runs the migrate verb against the fixture repository.</summary>
    private CapturedConsoleExecution<int> Migrate(string targets, bool dryRun = false) =>
        MigrateAt(Repo, targets, dryRun);

    /// <summary>
    /// Runs the migrate verb against a root the case names, rather than the fixture repository — the
    /// fake home, for the cases about which tree the operator pointed the command at.
    /// </summary>
    private CapturedConsoleExecution<int> MigrateAt(string root, string targets, bool dryRun = false) =>
        ProcessConsoleCapture.Run(() => new DocsMcpMigrateCommand(() => FakeHome).Execute(
            null!,
            new DocsMcpMigrateSettings { Path = root, Target = targets, DryRun = dryRun }));

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

    /// <summary>The path a fake-home case reads back: a user-scope file under the fake home.</summary>
    private string InHome(string relativePath) =>
        Path.Combine(FakeHome, relativePath.Replace('/', Path.DirectorySeparatorChar));

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
