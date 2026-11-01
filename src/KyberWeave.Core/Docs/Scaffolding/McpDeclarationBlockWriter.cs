using System.Text.RegularExpressions;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>
/// The <c>mcp:</c> declaration <c>docs init</c> scaffolds: the block itself, whether a host
/// configuration already carries one, and the textual append that adds it when it does not.
/// </summary>
/// <remarks>
/// <para>
/// The block is appended as text, never round-tripped through a YAML emitter, for the same
/// reason <see cref="ReviewBlockWriter"/> appends its section that way: a round trip drops the
/// operator's comments, reorders keys, and loses unknown ones. Only the appended lines are
/// new; every other byte of the file is returned verbatim, in the file's own newline style.
/// </para>
/// <para>
/// The section is written once and afterwards belongs to the host. <c>docs init</c> appends it
/// only when the file has none and never rewrites one that exists, <c>--force</c> included:
/// regenerating it would replace the servers a repository actually depends on with the tool's
/// own guess. The rule has a second edge here that the review block does not carry on its own —
/// the host configuration is on this repository's <c>review.policy.always-human</c> list, so a
/// change to it escalates for human review by path alone, before any finding is weighed. A
/// rewrite on every run would raise that escalation against a change nobody asked for.
/// </para>
/// <para>
/// Nothing here reads a configuration. The declaration is independent of Squad's
/// <c>toolchain.yml</c> roster and of <c>products/kyber-squad/mcp.json</c>: what a repository's
/// agent instructions depend on is the repository's own claim, held in this section.
/// </para>
/// </remarks>
public static partial class McpDeclarationBlockWriter
{
    /// <summary>
    /// The declaration as the design renders it: <c>kyber-weave</c> declared first-party and
    /// required, the third-party example commented out, and the always-human consequence stated
    /// in the header comment.
    /// </summary>
    /// <remarks>
    /// One constant is the single source of the block's text. The scaffolder's fresh-file
    /// template interpolates it and <see cref="AppendBlock"/> appends it, so a newly created
    /// host configuration and an appended section cannot drift apart.
    /// </remarks>
    public const string DeclarationYaml = """
        mcp:
          # The MCP servers this repository's agent instructions rely on. `docs init` wires these
          # into the harnesses it detects; the declaration is host-owned from the moment it exists
          # and docs init never edits it again. This file is on the review.policy.always-human
          # list in this repository, so every change here escalates for human review by path alone.
          required-servers:
            kyber-weave:
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
    /// True when <paramref name="configText"/> already carries a top-level <c>mcp:</c> key. The
    /// key must start in column zero, so a commented <c># mcp:</c> does not count, and a line
    /// break is any of CRLF, LF or a lone CR, so a file written with any of them is read the
    /// same way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check is textual rather than a YAML load, and the anchor is what makes that safe: the
    /// answer is "a column-zero <c>mcp:</c> appears", not "the document has a top-level
    /// <c>mcp</c> key". A snapshot taken with the parser would have to survive a document this
    /// tool did not write — an operator's file may already be invalid YAML — and the check's one
    /// job is to answer before anything is appended. The cost of being wrong is asymmetric, so
    /// the direction is chosen: a missed section appends a second <c>mcp:</c> and makes the
    /// document invalid YAML, while an extra match only declines to append, and the operator adds
    /// the section by hand.
    /// </para>
    /// <para>
    /// The known limits follow from the anchor. A multi-line quoted scalar may continue at column
    /// zero — <c>note: "first line</c> then <c>mcp: second line"</c> — and a second YAML document
    /// after <c>---</c> may open with the key. Both read as present, so the block is not appended;
    /// neither can produce the duplicate section a line-ending-blind match produced. A root-level
    /// flow mapping — <c>{"mcp": {...}}</c>, which is valid YAML — reads as absent, because the key
    /// starts no line. A document written in that style is outside what a block-section append can
    /// serve, and this check does not claim otherwise.
    /// </para>
    /// </remarks>
    public static bool HasMcpSection(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);
        return McpKey().IsMatch(configText);
    }

    /// <summary>
    /// Returns <paramref name="configText"/> with the declaration appended after one blank line, in
    /// the file's existing newline style, ending the file with exactly one newline after the block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every byte of <paramref name="configText"/> is the operator's, returned unchanged — trailing
    /// blank lines included. Collapsing their tail to a single separator would be a byte this tool
    /// edited that it was never asked to edit, and the host configuration's diff would then show a
    /// change nobody made; the block is separated from whatever whitespace the file ends with
    /// instead, so only the appended lines are new.
    /// </para>
    /// <para>
    /// The style those lines are written in is decided by <see cref="HostConfigYaml.DetectNewline"/>,
    /// the same call the <c>ontology:</c> edit in this file makes. Two appends to one file that
    /// disagreed about its line endings would leave the operator a document in two styles.
    /// </para>
    /// </remarks>
    public static string AppendBlock(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);

        string newline = HostConfigYaml.DetectNewline(configText);
        string block = newline == "\n"
            ? DeclarationYaml
            : DeclarationYaml.Replace("\n", newline, StringComparison.Ordinal);

        return configText + Separator(configText, newline) + block + newline;
    }

    /// <summary>
    /// What stands between the operator's last byte and the block: nothing for an empty file or one
    /// that already ends with a blank line, one line break for a file that ends with a break, and a
    /// break plus one for a file that ends mid-line.
    /// </summary>
    private static string Separator(string configText, string newline)
    {
        if (configText.Length == 0 || EndsWithBlankLine(configText))
            return string.Empty;

        return IsLineBreak(configText[^1]) ? newline : newline + newline;
    }

    /// <summary>
    /// True when the file's last two line breaks are adjacent, so the tail already separates the
    /// block by a blank line and another one would add a gap the operator never wrote.
    /// </summary>
    private static bool EndsWithBlankLine(string configText)
    {
        int index = configText.Length - 1;
        if (index < 0 || !IsLineBreak(configText[index]))
            return false;

        index = configText[index] == '\n' && index > 0 && configText[index - 1] == '\r'
            ? index - 2
            : index - 1;

        return index >= 0 && IsLineBreak(configText[index]);
    }

    private static bool IsLineBreak(char value) => value is '\r' or '\n';

    /// <summary>
    /// The anchor is a line start rather than <see cref="RegexOptions.Multiline"/>'s <c>^</c>,
    /// which in .NET matches only after <c>\n</c>: a file written with lone <c>\r</c> endings
    /// would then never be recognised, and the append that follows would write a second
    /// <c>mcp:</c> into it. Any of CRLF, LF or CR is a line start this matches.
    /// </summary>
    [GeneratedRegex(@"(?:\A|[\r\n])mcp[ \t]*:")]
    private static partial Regex McpKey();
}
