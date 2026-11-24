using System.Reflection;
using System.Text.RegularExpressions;

using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Tests;
using KyberWeave.Tests.Fakes;
using ModelContextProtocol.Server;
using Xunit;

namespace KyberWeave.Mcp.Tests;

/// <summary>
/// T6 RED — the <c>load_skill</c> tool answers a caller's request for one KW skill by name (decision D13). It
/// returns the provenance line, the <c>skill:</c> header a worker's reply is read by, the pool and folder the skill
/// came from, the paths of its reference files without their contents, and the body of <c>SKILL.md</c>.
/// </summary>
/// <remarks>
/// The tool is called through its <c>[McpServerTool]</c> name, the idiom the coding-standard tests use, so a rename
/// fails here rather than leaving the suite green while agents call a name the server no longer exposes.
/// </remarks>
public sealed class McpLoadSkillTests : IDisposable
{
    private const string ToolName = "load_skill";
    private const string SkillsPool = "6-Docs/standards/skills";

    private const string UsageMarker = "Pass the name of a KW skill";

    /// <summary>The provenance line's shape, the one every docs tool reply leads with.</summary>
    private static readonly Regex ProvenancePattern = new(
        "^provenance: root=(?<root>.+?) branch=\\S+ repo=.+? rev=(?<rev>[0-9a-f]{4,40}|unavailable) "
        + "dirty=(?<dirty>yes|no|unknown) documents=(?<documents>\\d+)$",
        RegexOptions.CultureInvariant);

    private readonly DocFixture _server = DocFixture.CreateInMemory().WithCatalog();

    public void Dispose()
    {
        _server.Dispose();
    }

    /// <summary>The body, the folder and the reference paths come back; the reference files' contents do not.</summary>
    [Fact]
    public void ItReturnsTheBodyTheFolderAndReferencePathsButNotTheirContents()
    {
        WriteSkill(_server, "plan-craft", "Plan craft for a change.", "SKILL BODY MARKER");
        _server.Write($"{SkillsPool}/plan-craft/references/checklist.md", "REFERENCE CONTENT MARKER");

        string reply = Load(Tools(_server), "plan-craft");

        Assert.Contains("skill: plan-craft\n", reply, StringComparison.Ordinal);
        Assert.Contains("source: repository\n", reply, StringComparison.Ordinal);
        Assert.Contains("folder: ", reply, StringComparison.Ordinal);
        Assert.Contains("references:\n- ", reply, StringComparison.Ordinal);
        Assert.Contains("checklist.md", reply, StringComparison.Ordinal);
        Assert.Contains("SKILL BODY MARKER", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCE CONTENT MARKER", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("description: Plan craft for a change.", reply, StringComparison.Ordinal);
    }

    /// <summary>A skill the repository has under its own pool answers from there, not from the shipped copy of the same name.</summary>
    [Fact]
    public void TheRepositoryCopyWinsOverTheShippedCopy()
    {
        WriteSkill(_server, "go-gates", "Repository version of the gates.", "REPOSITORY COPY MARKER");

        string reply = Load(Tools(_server), "go-gates");

        Assert.Contains("source: repository\n", reply, StringComparison.Ordinal);
        Assert.Contains("REPOSITORY COPY MARKER", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("source: shipped", reply, StringComparison.Ordinal);
    }

    /// <summary>A skill only the product ships is materialized into a folder the reply names, with no materialization failure.</summary>
    [Fact]
    public void AShippedSkillReportsItsMaterializedFolder()
    {
        string reply = Load(Tools(_server), "go-gates");

        Assert.Contains("skill: go-gates\n", reply, StringComparison.Ordinal);
        Assert.Contains("source: shipped\n", reply, StringComparison.Ordinal);
        Assert.Contains("folder: ", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("(not materialized", reply, StringComparison.Ordinal);
    }

    /// <summary>An unknown name is an answer that lists the skills that exist, not a tool error.</summary>
    [Fact]
    public void AnUnknownNameListsTheSkillsThatExistAndIsNotAFailure()
    {
        WriteSkill(_server, "plan-craft", "Plan craft.", "BODY");

        string reply = Load(Tools(_server), "no-such-skill");

        Assert.Contains("No KW skill is named 'no-such-skill'. Skills that exist: ", reply, StringComparison.Ordinal);
        Assert.Contains("plan-craft", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pool too large to name whole is named capped, with the remainder counted and the far end
    /// of the pool absent, so the reply stays bounded however many skills a checkout has.
    /// </summary>
    [Fact]
    public void AnUnknownNameCapsTheSkillsThatExistAndCountsTheRest()
    {
        for (int index = 1; index <= 25; index++)
        {
            WriteSkill(_server, $"skill-{index:00}", "Filler skill.", "BODY");
        }

        string reply = Load(Tools(_server), "no-such-skill");

        Assert.Contains("Skills that exist: ", reply, StringComparison.Ordinal);
        Assert.Contains("(+", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("skill-25", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A body longer than the section cap is cut at the cap with the truncation named, so one huge
    /// SKILL.md cannot blow the reply.
    /// </summary>
    [Fact]
    public void ABodyOverTheSectionCapIsTruncated()
    {
        WriteSkill(_server, "big-body", "A long body.", new string('x', 7000) + "TAIL-MARKER");

        string reply = Load(Tools(_server), "big-body");

        Assert.Contains("… truncated at 6000 characters.", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("TAIL-MARKER", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// A skill whose SKILL.md ends at its frontmatter is still found, with the empty body named
    /// rather than answered as blank space after the references.
    /// </summary>
    [Fact]
    public void AnEmptyBodyIsNamedRatherThanAnsweredAsBlankSpace()
    {
        _server.Write($"{SkillsPool}/empty-body/SKILL.md", "---\nname: empty-body\ndescription: No body.\n---\n");

        string reply = Load(Tools(_server), "empty-body");

        Assert.Contains("skill: empty-body\n", reply, StringComparison.Ordinal);
        Assert.Contains("(this SKILL.md carries no text below its frontmatter)", reply, StringComparison.Ordinal);
    }

    /// <summary>A skill whose frontmatter lacks a required field names the field, and is still an answer rather than a failure.</summary>
    [Fact]
    public void AMalformedSkillNamesWhatIsMissingAndIsNotAFailure()
    {
        _server.Write($"{SkillsPool}/no-desc/SKILL.md", "---\nname: no-desc\n---\n\nBody\n");

        string reply = Load(Tools(_server), "no-desc");

        Assert.Contains("The KW skill 'no-desc' in ", reply, StringComparison.Ordinal);
        Assert.Contains("cannot be loaded: its SKILL.md has no description", reply, StringComparison.Ordinal);
    }

    /// <summary>A SKILL.md whose frontmatter cannot be parsed reads as a parse failure, never as "has no &lt;error&gt;".</summary>
    [Fact]
    public void AnUnparseableFrontmatterIsReportedAsAReasonNotAMissingField()
    {
        _server.Write($"{SkillsPool}/broken/SKILL.md", "---\nname: [unclosed\n---\n\nBody\n");

        string reply = Load(Tools(_server), "broken");

        Assert.Contains("The KW skill 'broken' in ", reply, StringComparison.Ordinal);
        Assert.Contains("cannot be loaded: ", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("SKILL.md has no ", reply, StringComparison.Ordinal);
    }

    /// <summary>A blank name is answered with the usage line, which names the argument it wants.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankNameIsAnsweredWithUsage(string name)
    {
        WriteSkill(_server, "plan-craft", "Plan craft.", "BODY");

        string reply = Load(Tools(_server), name);

        Assert.Contains(UsageMarker, reply, StringComparison.Ordinal);
    }

    /// <summary>Every reply leads with the provenance line, and the line after it is the <c>skill:</c> header.</summary>
    [Fact]
    public void TheReplyStartsWithTheProvenanceLine()
    {
        WriteSkill(_server, "plan-craft", "Plan craft.", "BODY");

        string[] lines = Load(Tools(_server), "plan-craft").Split('\n');

        Assert.Matches(ProvenancePattern, lines[0]);
        Assert.Equal("skill: plan-craft", lines[1]);
    }

    /// <summary>A per-call projectPath is served by the tools bound to that checkout, not by this server's own corpus.</summary>
    [Fact]
    public void AProjectPathRoutesToThatCheckout()
    {
        using DocFixture work = DocFixture.CreateInMemoryBeside(_server, "work");
        WriteSkill(_server, "plan-craft", "Plan craft.", "SERVER BODY MARKER");
        WriteSkill(work, "plan-craft", "Plan craft.", "WORK BODY MARKER");

        DocsTools workTools = Tools(work);
        DocsRootRegistry roots = new(
            _server.Root,
            root => workTools,
            new SharedGitDirectory(Path.Combine(_server.Root, ".git")),
            _server.Memory!,
            _server.Paths,
            _server.Environment);
        DocsTools serverTools = Tools(_server, roots);

        string reply = Load(serverTools, "plan-craft", work.Root);

        Assert.Contains("WORK BODY MARKER", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("SERVER BODY MARKER", reply, StringComparison.Ordinal);
    }

    private static DocsTools Tools(DocFixture fixture, DocsRootRegistry? roots = null)
    {
        KyberWeaveConfig config = fixture.Config().Load(fixture.Root);
        DocumentIndexHost host = new(
            fixture.Root,
            () => FakeCodeGraphResolver.WithSymbols(),
            () => fixture.Loader(config.Ontology).Load(),
            fixture.Store,
            fixture.Paths,
            fixture.Environment,
            config.Ontology.DocsRoots,
            config.Ontology.ResolvedCatalogPath);
        return DocsToolsComposition.Create(host, roots: roots, services: fixture.McpServices());
    }

    private static void WriteSkill(DocFixture fixture, string name, string description, string body) =>
        fixture.Write($"{SkillsPool}/{name}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");

    private static string Load(DocsTools tools, string name, string? projectPath = null)
    {
        MethodInfo tool = typeof(DocsTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == ToolName);
        return (string)tool.Invoke(tools, [name, projectPath])!;
    }

    /// <summary>Every checkout here shares one git directory, so the registry treats them as worktrees of one repository.</summary>
    private sealed class SharedGitDirectory(string commonDirectory) : IGitWorktreeIdentity
    {
        public bool TryRoots(string directory, out string? topLevel, out string? commonDirectoryOut, out string? failure)
        {
            topLevel = directory;
            commonDirectoryOut = commonDirectory;
            failure = null;
            return true;
        }
    }
}
