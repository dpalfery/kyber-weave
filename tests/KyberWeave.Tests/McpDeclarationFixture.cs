namespace KyberWeave.Tests;

/// <summary>
/// Edits the <c>mcp:</c> declaration <c>docs init</c> scaffolds, so a test can arrange the one thing
/// the docs server's CodeGraph refresh depends on: whether the repository requires CodeGraph.
/// </summary>
/// <remarks>
/// <para>
/// The scaffolded block declares <c>kw-docs</c> and leaves the third-party example commented out, so a
/// repository that depends on CodeGraph declares it the way an operator does — by uncommenting that
/// entry. The entry is what <c>mcp.required-servers</c> reads for every third-party server, so a test
/// that wrote a declaration by hand would be pinning a shape the tool does not produce.
/// </para>
/// <para>
/// The edit is textual for the same reason the scaffolder appends its block as text: a round trip
/// through a YAML emitter drops the operator's comments and reorders the document, and the host
/// configuration a test arranges would then differ from the one an operator has.
/// </para>
/// </remarks>
internal static class McpDeclarationFixture
{
    /// <summary>The host configuration <c>docs init</c> writes, relative to the repository root.</summary>
    private const string ConfigRelativePath = ".kyber-weave/kyber-weave.yml";

    /// <summary>The indentation the scaffold puts each entry under <c>required-servers</c> at.</summary>
    private const string EntryIndent = "    ";

    /// <summary>How the scaffold writes the entry it leaves for the operator: one comment per line.</summary>
    private const string EntryCommentPrefix = EntryIndent + "# ";

    /// <summary>The line the scaffolded CodeGraph entry opens with, still commented out.</summary>
    private const string CommentedCodeGraphEntry = EntryIndent + "# codegraph:";

    /// <summary>Declares <c>codegraph</c> among the MCP servers <paramref name="root"/> requires.</summary>
    /// <param name="root">The repository root whose scaffolded host configuration is edited.</param>
    /// <exception cref="InvalidOperationException">The configuration carries no commented CodeGraph entry.</exception>
    public static void RequireCodeGraph(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        string path = Path.Combine(root, ConfigRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string config = File.ReadAllText(path);
        string newline = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        List<string> lines = [.. config.Split(newline)];

        int entryStart = lines.IndexOf(CommentedCodeGraphEntry);
        if (entryStart < 0)
        {
            throw new InvalidOperationException(
                $"'{ConfigRelativePath}' in '{root}' carries no commented 'codegraph' entry, so this fixture "
                + "cannot declare it as required. The scaffolded declaration changed shape; make this fixture "
                + "write the declaration the scaffold now emits.");
        }

        // The entry is a key plus its indented fields, each written as its own comment. The walk stops
        // at the first line outside that run, so the prose comments above the entry and the section
        // that follows it are left as the scaffold wrote them.
        for (int index = entryStart;
             index < lines.Count && lines[index].StartsWith(EntryCommentPrefix, StringComparison.Ordinal);
             index++)
        {
            lines[index] = EntryIndent + lines[index][EntryCommentPrefix.Length..];
        }

        File.WriteAllText(path, string.Join(newline, lines));
    }
}
