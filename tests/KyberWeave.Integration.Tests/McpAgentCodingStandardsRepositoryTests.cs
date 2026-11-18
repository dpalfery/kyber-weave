using System.Reflection;
using System.Text.RegularExpressions;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Mcp;
using KyberWeave.Tests;
using KyberWeave.Tests.Configuration;
using KyberWeave.Tests.Corpus;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The <c>docs_get_coding_standard_for_technology</c> tool over this repository's own shipped standards, on the real
/// disk. The unit cases for the tool's behaviour run in memory in <c>McpAgentCodingStandardsTests</c>; these three
/// measure the answer a worker receives from the real corpus, whose size and wording a fixture cannot reproduce.
/// </summary>
public sealed class McpAgentCodingStandardsRepositoryTests
{
    private static readonly ICorpusSource Shipped = CheckoutCorpusSource.Checkout;

    private const string ToolName = "docs_get_coding_standard_for_technology";

    /// <summary>
    /// Wording a response may use for "this cut is the ordinary outcome". A set rather than one
    /// phrase, because the requirement is the meaning: a standard larger than the budget is cut
    /// on purpose, not a document with a gap in it.
    /// </summary>
    private static readonly Regex CutIsExpected = new(
        "\\b(?:expected|expect|normal|by design|deliberate(?:ly)?|routine|as intended|not a failure)\\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A note that could be talking about the budget, the cut, or the omitted sections.</summary>
    private static readonly Regex NamesTheBudgetOrTheCut = new(
        "\\b(?:charBudget|budget|omitted|cut|truncated|characters)\\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The tool surface over this repository's own corpus, built once. The tests that use it are
    /// about the standards this repository actually ships — how large they are, which sections
    /// they hold, and which of those a caller's topic has to reach — so a fixture would measure
    /// the fixture instead of the answer a worker receives.
    /// </summary>
    private static readonly Lazy<DocsTools> RepositorySurface = new(BuildRepositorySurface);

    /// <summary>
    /// The tool's own documented example, and the call the whole tool exists for: a caller about
    /// to write error handling in this repository's C# asks for <c>error handling</c> and is
    /// returned the section headed <c>Errors and diagnostics</c>. It currently returns no prose
    /// at all, and tells the caller so — naming every heading it did not match, including that
    /// one. The topic was vectorised as a phrase, so the pair <c>errorhandling</c>, which no
    /// section states, carried most of the query's weight and pushed the one section that does
    /// discuss errors under the relevance floor.
    /// </summary>
    /// <remarks>
    /// Measured against this repository's own standard rather than a fixture, because the margin
    /// is what makes the bug: the section scores just under the floor with the fused pair in the
    /// query and just over it without, and a fixture corpus cannot reproduce that ratio. Its
    /// inverse document frequencies are all about equal on a handful of documents, while here the
    /// compound <c>errorhandling</c> appears in none of the corpus and <c>error</c> appears in
    /// many, which is the asymmetry that costs the section its place.
    /// </remarks>
    [Fact]
    public void ATopicOfTwoWordsReachesTheSectionThatDiscussesThoseWords()
    {
        string response = Ask(RepositorySurface.Value, "csharp", "error handling");

        Assert.Contains("## Errors and diagnostics", response, StringComparison.Ordinal);
        Assert.DoesNotContain("no section matches", response, StringComparison.Ordinal);
    }

    /// <summary>
    /// The test standard is larger than the 12,000-character default, so what the cut drops names
    /// sections the reader is not getting. The response names them, and says the cut is expected: a
    /// standard larger than the budget is the ordinary case and the reader is not missing anything
    /// the tool failed to find.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing and only one of them is there today. Naming what was omitted
    /// tells a reader that something exists; it does not tell them the omission is a size cut
    /// rather than a gap, so the reader who is not told opens the file to see what is missing —
    /// the whole cost this tool removes. Asserted against this repository's own test standard
    /// because its size against the default budget is the case, not a fixture that would have to
    /// be built to be just as large.
    /// </remarks>
    [Fact]
    public void AStandardLargerThanTheDefaultBudgetNamesTheCutAndSaysTheCutIsExpected()
    {
        string response = Ask(RepositorySurface.Value, "test");

        string omission = Assert.Single(OmissionLines(response));

        // The sections the cut drops are asserted from the standard itself rather than by name:
        // a renamed heading (slice 7 renamed `## Commands` to `## Completion`) would otherwise make
        // this pin fail for the rename rather than for a lost omission line. What must hold is that
        // every omitted section is named, whatever the headings are called today.
        string standard = Shipped.ReadText("docs/standards/test/test.standards.md");
        int namesAt = omission.IndexOf(':', StringComparison.Ordinal);
        string named = namesAt >= 0 ? omission[(namesAt + 1)..] : omission;
        named = named.TrimEnd(']', '\r', '\n', ' ');
        string[] omitted = named
            .Split(" · ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
        Assert.NotEmpty(omitted);
        Assert.All(
            omitted,
            section => Assert.Contains($"## {section}", standard, StringComparison.Ordinal));
        Assert.DoesNotContain("not matching this topic", omission, StringComparison.Ordinal);

        AssertTheCutIsSaidToBeExpected(response);
    }

    [Fact]
    public void TheRepositoryBundleCarriesItsRulesAndSkillsForEveryInstalledTechnology()
    {
        string[] installed = ["csharp", "test", "typescript", "rust", "tauri", "go", "github-actions"];

        foreach (string technology in installed)
        {
            string response = Ask(RepositorySurface.Value, technology);

            Assert.Contains($"## {technology} rules", response, StringComparison.Ordinal);
            Assert.Contains($"## {technology} skills", response, StringComparison.Ordinal);
        }

        Assert.Contains("## Shared rules", Ask(RepositorySurface.Value, "csharp"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts some line says the cut is expected — that the standard is larger than the budget and
    /// the omission is the ordinary outcome — as well as naming what was cut, which is the other
    /// half of the same sentence's job. Without the first half a reader opens the file to find
    /// what is missing, which is the cost this response exists to avoid.
    /// </summary>
    private static void AssertTheCutIsSaidToBeExpected(string response)
    {
        List<string> said = new();
        foreach (string rawLine in response.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (!CutIsExpected.IsMatch(line)) continue;
            if (!NamesTheBudgetOrTheCut.IsMatch(line)) continue;
            said.Add(line);
        }

        Assert.True(
            said.Count > 0,
            "A standard larger than the budget must say the cut is expected — that asking for "
            + "12000 characters of a longer standard cuts it as a matter of course, rather than "
            + "leaving a gap the reader has to open the file to fill. Keep the existing "
            + "'[omitted for space, ask again with a larger charBudget: …]' marker and the "
            + "'not matching this topic' marker as they are, and say the cut is expected as well. "
            + $"Response was: {response}");
    }

    /// <summary>The marker naming the sections the budget cut, one line, so a response naming them twice fails here.</summary>
    private static List<string> OmissionLines(string response)
    {
        List<string> lines = new();
        foreach (string rawLine in response.Split('\n'))
        {
            if (rawLine.StartsWith("[omitted for space", StringComparison.Ordinal))
                lines.Add(rawLine.TrimEnd('\r'));
        }

        return lines;
    }

    private static string Ask(
        DocsTools tools,
        string technology,
        string? topic = null,
        int charBudget = DocumentIndex.DefaultCharBudget)
    {
        MethodInfo tool = FindMcpTool();
        return (string)tool.Invoke(tools, [technology, topic, charBudget, null])!;
    }

    private static MethodInfo FindMcpTool()
    {
        MethodInfo? method = typeof(DocsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate =>
                candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == ToolName);
        Assert.True(method is not null, $"DocsTools must expose an MCP tool named '{ToolName}'.");
        return method;
    }

    /// <summary>
    /// The tool surface over this repository's own corpus. Built once, because the two callers
    /// both need the real documents and a second build would parse 192 of them again for the same
    /// answer.
    /// </summary>
    private static DocsTools BuildRepositorySurface()
    {
        string root = Shipped.Root;
        KyberWeaveConfig config = ConfigFileReader.Load(root);
        DocumentIndexHost host = new(
            root,
            () => DocsPorts.CodeGraph(root),
            () => RealDocumentLoader.Create(root, config.Ontology).Load(),
            DocsPorts.Files,
            DocsPorts.Paths,
            DocsPorts.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
        return DocsToolsComposition.Create(host);
    }
}
