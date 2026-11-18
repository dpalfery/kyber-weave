using System.Text;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
///     <c>docs init</c> gives a host the <c>mcp:</c> declaration its agent instructions rely on,
///     and then hands the section over for good.
/// </summary>
/// <remarks>
///     The host configuration is host-owned from the moment it exists, so a
///     tool that rewrote the declaration on its next run would raise an escalation
///     against a change nobody asked for. The section is therefore written once — embedded when
///     the file is created, appended when the file has none — and afterwards it belongs to the
///     host, <c>--force</c> included.
/// </remarks>
public sealed class McpDeclarationBlockWriterTests
{
    /// <summary>
    ///     The block exactly as the design renders it: one server declared, and the third-party
    ///     example commented out.
    /// </summary>
    private const string DeclarationBlock =
        """
        mcp:
          # The MCP servers this repository's agent instructions rely on. `docs init` wires these
          # into the harnesses it detects; the declaration is host-owned from the moment it exists
          # and docs init never edits it again.
          required-servers:
            kw-docs:
              command: [kyber-weave-mcp, --repo-root, .]
              provenance: first-party
              level: required
            # A third-party server is declared the same way; its binary is never installed by
            # Kyber-Weave. Uncomment when the repository's instructions require it:
            # codegraph:
            #   command: [codegraph, serve, --mcp]
            #   provenance: third-party
            #   level: recommended
            #   install: codegraph init .   # printed verbatim when the binary is absent
        """;

    /// <summary>
    ///     An operator's own configuration: their comment, the stack this host declares, and no
    ///     <c>mcp:</c> section. The docs root already matches, so adopting it changes nothing
    ///     but the appended block.
    /// </summary>
    private const string HandMaintainedConfig =
        """
        # Ours, not the tool's.
        ontology:
          docs-root: docs
          technologies:
            - go
        """;

    private readonly MemoryScaffoldHost _host = new();

    private const string ConfigPath = ".kyber-weave/kyber-weave.yml";

    private const string IgnorePath = ".kyber-weave/.gitignore";

    /// <summary>
    ///     A host that runs <c>docs init</c> for the first time is told which server its
    ///     instructions may lean on — exactly one, the one Kyber-Weave ships — and the
    ///     third-party example arrives commented out, so nothing claims a dependency the
    ///     repository has not made.
    /// </summary>
    [Fact]
    public void AFreshHostConfigDeclaresExactlyTheFirstPartyServer()
    {
        _host.Scaffold();

        KyberWeaveConfig config = _host.LoadConfig();
        KeyValuePair<string, RequiredMcpServer> declared = Assert.Single(config.Mcp.RequiredServers);

        // The scaffold names the shipped server kw-docs. The old kyber-weave key is retired and only
        // existing installs carry it, so a fresh docs init must never write it.
        Assert.Equal("kw-docs", declared.Key);
        Assert.Equal("kw-docs", declared.Value.Name);
        Assert.Equal(["kyber-weave-mcp", "--repo-root", "."], declared.Value.Command);
        Assert.Equal(McpProvenance.FirstParty, declared.Value.Provenance);
        Assert.Equal(McpRequirementLevel.Required, declared.Value.Level);

        Assert.Equal(DeclarationBlock, ReadDeclarationSection(_host.Read(ConfigPath)));
    }

    /// <summary>
    ///     The scaffolded declaration text must not cite the retired always-human or needs-human
    ///     review policies.
    /// </summary>
    [Fact]
    public void ScaffoldedMcpDeclarationDoesNotCiteRetiredAlwaysHumanOrNeedsHumanPolicy()
    {
        _host.Scaffold();

        string configContent = _host.Read(ConfigPath);

        Assert.DoesNotContain("always-human", configContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("needs-human", configContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("escalates for human review", configContent, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("always-human", McpDeclarationBlockWriter.DeclarationYaml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("needs-human", McpDeclarationBlockWriter.DeclarationYaml,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("escalates for human review", McpDeclarationBlockWriter.DeclarationYaml,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The append is textual, in the file's own newline style, and it adds nothing but the
    ///     block. Round-tripping the operator's YAML would reorder keys and drop their comments;
    ///     rewriting the file to tidy it would show up in review as a change nobody made.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AppendingTheBlockLeavesEveryByteBeforeItUntouched(string newline)
    {
        string original = string.Join(
            newline, "# Ours, not the tool's.", "ontology:", "  docs-root: docs", string.Empty);

        string updated = McpDeclarationBlockWriter.AppendBlock(original);

        string expected = original + newline + InNewlineStyle(DeclarationBlock, newline) + newline;
        Assert.Equal(expected, updated);
    }

    /// <summary>
    ///     The same guarantee through the seam <c>docs init</c> actually uses: an existing
    ///     configuration without the section gains it, and the bytes before it are the operator's
    ///     original file, unchanged.
    /// </summary>
    [Fact]
    public void DocsInitAppendsTheDeclarationWhenTheHostConfigHasNone()
    {
        string original = HandMaintainedConfig + "\n";
        WriteHostConfig(original);
        byte[] originalBytes = _host.Memory.ReadAllBytes(_host.FullPath(ConfigPath));

        _host.Scaffold(reviewGates: false);

        byte[] appended = Encoding.UTF8.GetBytes("\n" + DeclarationBlock + "\n");
        Assert.Equal(originalBytes.Concat(appended), _host.Memory.ReadAllBytes(_host.FullPath(ConfigPath)));
        Assert.Equal(
            "kw-docs",
            Assert.Single(_host.LoadConfig().Mcp.RequiredServers).Key);
    }

    /// <summary>
    ///     A host-owned declaration is never rewritten, with or without <c>--force</c>.
    ///     Regenerating it would replace the servers this repository actually depends on with the
    ///     tool's guess, rewriting operator state without permission.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExistingDeclarationSurvivesDocsInitUnchanged(bool force)
    {
        const string handOwned =
            """
            # Ours, not the tool's.
            ontology:
              docs-root: docs
            mcp:
              required-servers:
                codegraph:
                  command: [codegraph, serve, --mcp]
                  provenance: third-party
                  level: recommended
            """;
        WriteHostConfig(handOwned);

        // The hand-owned declaration must load, or the scaffold preflight would reject the file
        // and this test would prove nothing about preservation.
        Assert.Equal(
            "codegraph",
            Assert.Single(_host.LoadConfig().Mcp.RequiredServers).Key);

        _host.Scaffold(force: force, reviewGates: false);

        Assert.Equal(handOwned, _host.Read(ConfigPath));
    }

    /// <summary>
    ///     A byte-order mark is a character, not a line break, so one written ahead of the first
    ///     line must not hide the declaration that starts immediately after it. Read as "start of
    ///     document" alone, the anchor lands on the mark and misses the key; the append that
    ///     follows then writes a second <c>mcp:</c>, which YAML refuses to load.
    /// </summary>
    [Fact]
    public void AByteOrderMarkBeforeTheFirstLineDoesNotHideTheDeclaration()
    {
        const string handOwned =
            "\uFEFF" +
            "mcp:\n" +
            "  required-servers:\n" +
            "    codegraph:\n" +
            "      command: [codegraph, serve, --mcp]\n" +
            "      provenance: third-party\n" +
            "      level: recommended\n";

        Assert.True(
            McpDeclarationBlockWriter.HasMcpSection(handOwned),
            "A byte-order mark ahead of the first line hid the declaration; the append would "
            + "write a second 'mcp:' key and leave the configuration unloadable.");
    }

    /// <summary>
    ///     The scaffold's own ignore file covers both local-state directories it owns: the
    ///     analysis cache beside it, and the receipts the wiring writes. A receipt that reached
    ///     version control would be a machine-local record committed as if it were a decision.
    /// </summary>
    [Fact]
    public void AFreshScaffoldIgnoresBothLocalStateDirectories()
    {
        _host.Scaffold(reviewGates: false);

        string[] lines = Lines(IgnorePath);
        Assert.Single(lines, line => string.Equals(line, "cache/", StringComparison.Ordinal));
        Assert.Single(lines, line => string.Equals(line, "receipts/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Merging the receipt entry must not disturb what the operator keeps in that file. The
    ///     existing <c>cache/</c> line is already effective here, so the merge has to add the
    ///     second entry without regenerating the first.
    /// </summary>
    [Fact]
    public void TheIgnoreMergeAddsReceiptsAndKeepsEveryOperatorEntry()
    {
        const string existing = "# operator entry\nlocal-notes/\ncache/\n";
        WriteIgnoreFile(existing);
        byte[] originalBytes = _host.Memory.ReadAllBytes(_host.FullPath(IgnorePath));

        _host.Scaffold(force: true, reviewGates: false);

        Assert.Equal(originalBytes, _host.Memory.ReadAllBytes(_host.FullPath(IgnorePath)).Take(originalBytes.Length));
        string[] lines = Lines(IgnorePath);
        Assert.Contains("# operator entry", lines);
        Assert.Contains("local-notes/", lines);
        Assert.Single(lines, line => string.Equals(line, "cache/", StringComparison.Ordinal));
        Assert.Single(lines, line => string.Equals(line, "receipts/", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A second run changes neither file: no second declaration, no duplicated ignore line.
    ///     Adoption that diffed on every re-run would make the repository look perpetually dirty
    ///     and would escalate the host configuration for review every time.
    /// </summary>
    [Fact]
    public void ReRunningDocsInitChangesNeitherTheDeclarationNorTheIgnoreFile()
    {
        _host.Scaffold(reviewGates: false);
        string configAfterFirstRun = _host.Read(ConfigPath);
        string ignoreAfterFirstRun = _host.Read(IgnorePath);

        _host.Scaffold(force: true, reviewGates: false);

        Assert.Equal(configAfterFirstRun, _host.Read(ConfigPath));
        Assert.Equal(ignoreAfterFirstRun, _host.Read(IgnorePath));
        Assert.Single(
            _host.Read(ConfigPath).Split('\n'),
            line => line.StartsWith("mcp:", StringComparison.Ordinal));
    }

    /// <summary>
    ///     The <c>mcp:</c> section exactly as written: from the column-zero key to whichever comes
    ///     first, the next column-zero key or a column-zero comment that opens a region of its own
    ///     after a blank line. The section's trailing newline is removed so the comparison is
    ///     against the block alone.
    /// </summary>
    private static string ReadDeclarationSection(string configText)
    {
        string[] lines = configText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int start = Array.FindIndex(lines, line => line.StartsWith("mcp:", StringComparison.Ordinal));
        Assert.True(
            start >= 0,
            $"The scaffolded host configuration has no top-level 'mcp:' section. Text:{Environment.NewLine}{configText}");

        int end = lines.Length;
        for (int index = start + 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Length == 0)
                continue;

            bool opensNextRegion = line[0] != ' '
                                   && (line[0] != '#' || lines[index - 1].Length == 0);
            if (opensNextRegion)
            {
                end = index;
                break;
            }
        }

        return string.Join('\n', lines, start, end - start).TrimEnd('\n');
    }

    private static string InNewlineStyle(string text, string newline)
    {
        return newline == "\n" ? text : text.Replace("\n", newline, StringComparison.Ordinal);
    }

    private void WriteHostConfig(string yaml) => _host.Write(ConfigPath, yaml);

    private void WriteIgnoreFile(string content) => _host.Write(IgnorePath, content);

    private string[] Lines(string relativePath) =>
        _host.Read(relativePath).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
