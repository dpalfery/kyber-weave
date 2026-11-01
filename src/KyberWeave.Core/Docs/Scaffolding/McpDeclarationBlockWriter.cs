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
    /// key must start in column zero, so a commented <c># mcp:</c> does not count.
    /// </summary>
    public static bool HasMcpSection(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);
        return McpKey().IsMatch(configText);
    }

    /// <summary>
    /// Returns <paramref name="configText"/> with the declaration appended after one blank line,
    /// in the file's existing newline style, ending the file with exactly one newline.
    /// </summary>
    /// <remarks>
    /// Every byte before the appended lines is the operator's, unchanged. Trailing blank lines
    /// collapse to the single blank line that separates the section, because leaving them would
    /// put the block an arbitrary distance from the file's last entry and produce a different
    /// result on every run that edited the tail.
    /// </remarks>
    public static string AppendBlock(string configText)
    {
        ArgumentNullException.ThrowIfNull(configText);

        string newline = configText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string block = newline == "\n"
            ? DeclarationYaml
            : DeclarationYaml.Replace("\n", newline, StringComparison.Ordinal);

        string prefix = configText.TrimEnd('\r', '\n');
        return prefix.Length == 0
            ? block + newline
            : prefix + newline + newline + block + newline;
    }

    [GeneratedRegex(@"^mcp[ \t]*:", RegexOptions.Multiline)]
    private static partial Regex McpKey();
}
