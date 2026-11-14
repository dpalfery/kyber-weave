using System.Reflection;
using System.Text.RegularExpressions;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the renamed MCP tool names and the length and character limits they must stay inside.
/// The names are read from the <c>[McpServerTool]</c> attributes by reflection, so the test sees
/// what a client sees on the wire rather than what a constant says.
/// </summary>
/// <remarks>
/// <para>
/// Limit sources, so a later reader can raise a limit with evidence rather than by guess:
/// </para>
/// <list type="bullet">
/// <item>The MCP specification and the Anthropic Messages API allow a tool name of up to 128 characters.</item>
/// <item>Cursor's limit of 60 characters on server key plus tool name comes from staff replies on its forum,
/// not from official Cursor documentation.</item>
/// <item>The 64-character limit on the qualified name <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> comes from
/// third-party documentation and older Claude Code reports.</item>
/// </list>
/// <para>
/// These are conservative guards. They hold every name inside the shortest limit any client has reported, so a
/// name that passes here is accepted by the strictest host this repository is known to serve. A name that breaks
/// one of these guards needs a source for a looser limit before the guard is relaxed.
/// </para>
/// </remarks>
public sealed class McpToolNameContractTests
{
    private const string DocsServer = "kw-docs";
    private const string ArbiterServer = "kw-arbiter";

    private static readonly Regex ToolNamePattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every renamed tool: the server key it is wired under, the name it carried before the rename, and the name it
    /// carries now. The C# method names do not change; only the declared MCP name does.
    /// </summary>
    private static readonly (string Server, string Retired, string Renamed)[] Renames =
    [
        (DocsServer, "docs_explore", "docs_answer_question_from_repo_documentation"),
        (DocsServer, "docs_for_symbol", "docs_find_documents_citing_code_symbol"),
        (DocsServer, "docs_glossary", "docs_look_up_glossary_term_definition"),
        (DocsServer, "docs_analysis_candidates", "docs_find_duplicates_conflicts_ambiguous_terms"),
        (DocsServer, "docs_validate", "docs_validate_governed_document_schema"),
        (DocsServer, "docs_drift", "docs_detect_drift_between_documents_and_code"),
        (DocsServer, "docs_status", "docs_get_source_repository_and_revision"),
        (DocsServer, "agent_coding_standards", "docs_get_coding_standard_for_technology"),
        (ArbiterServer, "arbiter_evaluate", "arbiter_evaluate_agent_dispatch_or_return"),
        (ArbiterServer, "arbiter_rules", "arbiter_list_effective_rules_by_trigger"),
        (ArbiterServer, "arbiter_status", "arbiter_check_configuration_and_key_readiness"),
    ];

    /// <summary>
    /// Tools added after the rename. They are new names, not renames, so they live apart from
    /// <see cref="Renames"/>, but their server must declare them too and the length limits apply to them.
    /// </summary>
    private static readonly (string Server, string Name)[] AddedTools =
    [
        (ArbiterServer, "arbiter_select_relevant_document_sections"),
        (ArbiterServer, "arbiter_classify_bot_review_comment"),
        (DocsServer, "docs_finalize_approved_plan_and_write_task_file"),
        (DocsServer, "docs_archive_plan_after_closeout_is_complete"),
    ];

    /// <summary>The docs server exposes exactly its eight renamed and two added tools, and the arbiter server its three renamed and two added.</summary>
    [Fact]
    public void TheDocsAndArbiterToolsDeclareExactlyTheRenamedNames()
    {
        string[] declaredDocs = DeclaredNames(typeof(DocsTools));
        string[] declaredArbiter = DeclaredNames(typeof(ArbiterTools));
        string[] expectedArbiter = Renames
            .Where(rename => rename.Server == ArbiterServer)
            .Select(rename => rename.Renamed)
            .Concat(AddedTools.Where(added => added.Server == ArbiterServer).Select(added => added.Name))
            .ToArray();
        string[] expectedDocs = Renames
            .Where(rename => rename.Server == DocsServer)
            .Select(rename => rename.Renamed)
            .Concat(AddedTools.Where(added => added.Server == DocsServer).Select(added => added.Name))
            .ToArray();

        List<string> missing = expectedDocs
            .Concat(expectedArbiter)
            .Where(name => !declaredDocs.Contains(name, StringComparer.Ordinal)
                && !declaredArbiter.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} of {Renames.Length + AddedTools.Length} expected tools are not declared by DocsTools or ArbiterTools: "
            + string.Join(", ", missing));

        Assert.Equal(
            expectedDocs.Order(StringComparer.Ordinal),
            declaredDocs.Order(StringComparer.Ordinal));
        Assert.Equal(
            expectedArbiter.Order(StringComparer.Ordinal),
            declaredArbiter.Order(StringComparer.Ordinal));
    }

    /// <summary>No retired name is still declared by either server, so a client cannot bind to the old name.</summary>
    [Fact]
    public void NoRetiredToolNameIsStillDeclared()
    {
        string[] declared = DeclaredNames(typeof(DocsTools))
            .Concat(DeclaredNames(typeof(ArbiterTools)))
            .ToArray();

        List<string> stillDeclared = Renames
            .Select(rename => rename.Retired)
            .Where(retired => declared.Contains(retired, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            stillDeclared.Count == 0,
            "These retired tool names are still declared: " + string.Join(", ", stillDeclared));
    }

    [Theory]
    [MemberData(nameof(RenamedTools))]
    public void EachRenamedToolNameMatchesThePatternAndHoldsNoDoubleUnderscore(string server, string tool)
    {
        Assert.Contains(tool, DeclaredBy(server), StringComparer.Ordinal);
        Assert.True(
            ToolNamePattern.IsMatch(tool),
            $"'{tool}' must match ^[a-zA-Z0-9_-]{{1,64}}$ so every client accepts it.");
        Assert.DoesNotContain("__", tool, StringComparison.Ordinal);
        Assert.DoesNotContain("__", server, StringComparison.Ordinal);
    }

    /// <summary>The server key plus the tool name stays within 60 characters (Cursor, see the class remarks).</summary>
    [Theory]
    [MemberData(nameof(RenamedTools))]
    public void ServerKeyPlusToolNameStaysWithinSixtyCharacters(string server, string tool)
    {
        Assert.Contains(tool, DeclaredBy(server), StringComparer.Ordinal);
        Assert.True(
            server.Length + tool.Length <= 60,
            $"'{server}' plus '{tool}' is {server.Length + tool.Length} characters; the limit is 60.");
    }

    /// <summary>The qualified name <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> stays within 64 characters (see the class remarks).</summary>
    [Theory]
    [MemberData(nameof(RenamedTools))]
    public void QualifiedNameStaysWithinSixtyFourCharacters(string server, string tool)
    {
        Assert.Contains(tool, DeclaredBy(server), StringComparer.Ordinal);
        string qualified = $"mcp__{server}__{tool}";

        Assert.True(
            qualified.Length <= 64,
            $"'{qualified}' is {qualified.Length} characters; the limit is 64.");
    }

    public static IEnumerable<object[]> RenamedTools() =>
        Renames.Select(rename => new object[] { rename.Server, rename.Renamed })
            .Concat(AddedTools.Select(added => new object[] { added.Server, added.Name }));

    /// <summary>The names the class serving <paramref name="server"/> declares: the docs class or the arbiter class.</summary>
    private static string[] DeclaredBy(string server) =>
        server == DocsServer
            ? DeclaredNames(typeof(DocsTools))
            : DeclaredNames(typeof(ArbiterTools));

    /// <summary>The MCP names a type declares through <c>[McpServerTool]</c>, read off the attributes.</summary>
    private static string[] DeclaredNames(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToArray();
}
