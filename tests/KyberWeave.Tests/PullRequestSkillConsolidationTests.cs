using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the single provider-aware <c>create-pull-request</c> skill that replaced
/// <c>create-pull-request-github</c>, the delivery of every provider file and script it and
/// <c>pr-review-fix-comments</c> route to, and the removal of one host project's facts from
/// canonical Squad source.
/// </summary>
/// <remarks>
/// The two pull-request skills used to contradict each other, supported GitHub only, and named
/// their scripts and provider files in code spans. The render closure follows Markdown links
/// only, so those files were packaged but never deployed. These tests hold each of those
/// failures closed.
/// </remarks>
public sealed class PullRequestSkillConsolidationTests
{
    private const string CombinedSkill = "create-pull-request";
    private const string ReviewSkill = "pr-review-fix-comments";
    private const string RetiredSkill = "create-pull-request-github";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

    private static readonly string[] CombinedSkillResources =
    [
        "providers/azure-devops.md",
        "providers/github.md",
        "scripts/github-create-pr.ps1",
        "scripts/github-create-pr.sh"
    ];

    private static readonly string[] ReviewSkillResources =
    [
        "providers/azure-devops.md",
        "providers/github.md"
    ];

    /// <summary>
    /// The resolution order both provider-aware skills must state, in this order. Sharing the
    /// order keeps a repository from being routed to GitHub by one skill and to Azure DevOps by
    /// the other.
    /// </summary>
    private static readonly string[] ProviderSelectionTokens =
    [
        "`github` / `gh` / `azdo` / `ado` / `azure-devops`",
        "`dev.azure.com`",
        "`visualstudio.com`",
        "`github.com`",
        "MCP server",
        "ask the user"
    ];

    private static readonly string[] ProviderContractHeadings =
    [
        "Identity",
        "Tool map",
        "Templates",
        "Linking work",
        "Multi-line descriptions",
        "Read-only or missing MCP",
        "After merge",
        "Link formats",
        "Sources"
    ];

    private static readonly string[] ToolMapSteps =
    [
        "List long-lived branches",
        "Read default branch",
        "Find an open PR for source and target",
        "Create the PR",
        "Update the PR",
        "Read the PR back",
        "Check CI status",
        "Link work items"
    ];

    /// <summary>
    /// One host project's facts that must not return to portable Squad source. Scopes name the
    /// top-level folders under <c>products/kyber-squad/</c> a rule applies to.
    /// </summary>
    private static readonly HostFactRule[] HostFactRules =
    [
        new("Denver", RegexOptions.IgnoreCase, ["skills", "agents", "standards"], "one host's example"),
        new(@"\bDEN\b", RegexOptions.None, ["skills", "agents", "standards"], "the same example's airport code"),
        new(@"\.kilo/kilo\.json", RegexOptions.None, ["skills", "agents", "standards"], "one host's MCP configuration path"),
        new("GITHUB_READ_ONLY", RegexOptions.None, ["skills", "agents", "standards"], "one host's MCP mode"),
        new(@"\bin this repo\b", RegexOptions.IgnoreCase, ["skills", "agents"], "describes one host's configuration"),
        new(@"\[Admin Desktop\]|\[Local Processor\]|\bdocling\b|x86 deployment|Azure AI Search indexer", RegexOptions.IgnoreCase, ["skills", "agents", "standards"], "one host's component examples"),
        new(@"Contracts\.Models", RegexOptions.None, ["skills", "agents"], "one host's project layout"),
        new("mcp_azuredevops_m_", RegexOptions.None, ["skills", "agents"], "one host's MCP server-name prefix")
    ];

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    public static TheoryData<SquadTarget, SquadDeploymentScope> RenderTargets()
    {
        TheoryData<SquadTarget, SquadDeploymentScope> data = new();
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            data.Add(target, SquadDeploymentScope.Project);
        }

        data.Add(SquadTarget.Claude, SquadDeploymentScope.Global);
        return data;
    }

    /// <summary>
    /// Prevents the retired GitHub-only skill from surviving alongside the combined skill, where
    /// its contradicting title, issue-link, and base-branch rules would still be routable.
    /// </summary>
    [Fact]
    public void CreatePullRequestGithubIsNotCanonicalAndShipsInNoPackage()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);

        Assert.DoesNotContain(source.Skills, skill => skill.Name == RetiredSkill);
        Assert.DoesNotContain(source.Bundle.SkillNames, name => name == RetiredSkill);
        Assert.False(
            Directory.Exists(Path.Combine(ProductRoot, "skills", RetiredSkill)),
            $"The retired skill directory 'skills/{RetiredSkill}' still exists.");

        using TempDirectory temp = new();
        string[] archives =
        [
            SquadPacker.PackApm(ProductRoot, temp.Path, "consolidation"),
            SquadPacker.PackPlugins(ProductRoot, temp.Path, "consolidation")
        ];
        foreach (string archivePath in archives)
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string[] retiredEntries = archive.Entries
                .Select(entry => entry.FullName)
                .Where(name => name.StartsWith($"skills/{RetiredSkill}/", StringComparison.Ordinal))
                .ToArray();
            Assert.True(
                retiredEntries.Length == 0,
                $"{Path.GetFileName(archivePath)} still packages: {string.Join(", ", retiredEntries)}");
        }
    }

    /// <summary>
    /// Prevents a provider file or script from being packaged but never deployed because a skill
    /// names it in a code span instead of linking it: the closure must be exactly the files the
    /// skill carries.
    /// </summary>
    [Theory]
    [InlineData(CombinedSkill)]
    [InlineData(ReviewSkill)]
    public void ProviderSkillsDeliverEveryFileOnDiskThroughTheirClosure(string skillName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadSkill skill = Assert.Single(source.Skills, candidate => candidate.Name == skillName);
        string skillDirectory = Path.Combine(ProductRoot, "skills", skillName);

        string[] onDisk = Directory.EnumerateFiles(skillDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(skillDirectory, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !string.Equals(path, "SKILL.md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] closure = skill.Resources
            .Select(resource => resource.RelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedResources(skillName), onDisk);
        Assert.Equal(ExpectedResources(skillName), closure);
    }

    /// <summary>
    /// Prevents any target, at either scope, from deploying a provider-aware skill without the
    /// provider files and scripts its instructions tell the agent to open, or from deploying the
    /// retired skill.
    /// </summary>
    [Theory]
    [MemberData(nameof(RenderTargets))]
    public async Task EveryTargetDeploysProviderFilesAndScriptsBesideTheSkill(
        SquadTarget target,
        SquadDeploymentScope scope)
    {
        using TempDirectory userScope = new();
        SquadRendererRegistry registry = new(AllRenderers());
        SquadRenderRequest request = new(
            SourceDirectory: ProductRoot,
            Targets: [target],
            Scope: scope,
            UserScopeDirectory: scope == SquadDeploymentScope.Global ? userScope.Path : null);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        string[] retiredPaths = result.Files
            .Select(file => file.RelativePath)
            .Where(path => path.Contains(RetiredSkill, StringComparison.Ordinal))
            .ToArray();
        Assert.True(retiredPaths.Length == 0, $"Rendered the retired skill: {string.Join(", ", retiredPaths)}");

        foreach (string skillName in new[] { CombinedSkill, ReviewSkill })
        {
            string principalSuffix = $"/{skillName}/SKILL.md";
            SquadDeploymentFile principal = Assert.Single(
                result.Files,
                file => file.RelativePath.EndsWith(principalSuffix, StringComparison.Ordinal));
            string outputDirectory = principal.RelativePath[..^"SKILL.md".Length];

            foreach (string resource in ExpectedResources(skillName))
            {
                string expectedPath = outputDirectory + resource;
                SquadDeploymentFile? rendered = result.Files.SingleOrDefault(file =>
                    string.Equals(file.RelativePath, expectedPath, StringComparison.Ordinal));
                Assert.True(rendered is not null, $"'{expectedPath}' was not rendered beside '{principal.RelativePath}'.");

                string sourceText = await File.ReadAllTextAsync(Path.Combine(ProductRoot, "skills", skillName, resource));
                Assert.Equal(Encoding.UTF8.GetBytes(NormalizeLineEndings(sourceText)), rendered.Content);
            }
        }
    }

    /// <summary>
    /// Prevents the two provider-aware skills from resolving a repository to different providers,
    /// and prevents either from naming its provider files without linking them.
    /// </summary>
    [Theory]
    [InlineData(CombinedSkill)]
    [InlineData(ReviewSkill)]
    public void ProviderSelectionMatchesPrReviewFixCommentsAndLinksBothProviders(string skillName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadSkill skill = Assert.Single(source.Skills, candidate => candidate.Name == skillName);
        Section selection = Assert.Single(
            H2Sections(skill.InstructionBody),
            section => section.Heading == "Provider Selection");

        int previous = -1;
        foreach (string token in ProviderSelectionTokens)
        {
            int index = selection.Body.IndexOf(token, previous + 1, StringComparison.Ordinal);
            Assert.True(
                index > previous,
                $"{skillName} § Provider Selection does not state '{token}' after the previous resolution step.");
            previous = index;
        }

        string[] links = LinkTargets(selection.Body);
        foreach (string provider in ReviewSkillResources)
        {
            Assert.True(
                links.Contains(provider, StringComparer.Ordinal),
                $"{skillName} § Provider Selection does not link '{provider}'; found [{string.Join(", ", links)}].");
            Assert.True(File.Exists(Path.Combine(ProductRoot, "skills", skillName, provider)));
        }
    }

    /// <summary>
    /// Prevents the combined skill from stating a convention twice or reintroducing a rule a host
    /// could not reverse: a fixed base branch, a fixed template path, the retired skill, or
    /// provider commands that belong in a provider file.
    /// </summary>
    [Fact]
    public void CreatePullRequestResolvesEachConventionOnceInTheNeutralLayer()
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        string body = Assert.Single(source.Skills, candidate => candidate.Name == CombinedSkill).InstructionBody;
        Section[] sections = H2Sections(body);

        AssertSectionStates(sections, "Target branch", "Never assume the default branch", "ask the user");
        AssertSectionStates(sections, "Title", "A host convention wins", "Default:");
        AssertSectionStates(sections, "Linked work", "A host convention wins", "closing");
        AssertSectionStates(sections, "Description", "host's pull request template");

        Assert.DoesNotContain(RetiredSkill, body, StringComparison.Ordinal);
        Assert.DoesNotContain(".github/PULL_REQUEST_TEMPLATE.md", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex("branch(es)? from `?develop`?", RegexOptions.IgnoreCase), body);

        string[] providerCommands = CodeText(body)
            .Where(line => line.StartsWith("gh ", StringComparison.Ordinal) ||
                           line.StartsWith("az ", StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            providerCommands.Length == 0,
            $"Provider commands belong in a provider file: {string.Join(" | ", providerCommands)}");
    }

    /// <summary>
    /// Prevents a provider file from omitting a step the neutral skill relies on, or from stating
    /// commands and tool names without citing where they were verified.
    /// </summary>
    [Theory]
    [InlineData("github")]
    [InlineData("azure-devops")]
    public void ProviderFileMeetsTheSharedContractAndCitesSources(string provider)
    {
        string path = Path.Combine(ProductRoot, "skills", CombinedSkill, "providers", provider + ".md");
        Assert.True(File.Exists(path), $"Provider file 'providers/{provider}.md' is missing.");
        Section[] sections = H2Sections(File.ReadAllText(path));

        string[] required = provider == "github"
            ? [.. ProviderContractHeadings, "Helper scripts"]
            : ProviderContractHeadings;
        string[] missing = required
            .Where(heading => sections.Count(section => section.Heading == heading) != 1)
            .ToArray();
        Assert.True(missing.Length == 0, $"providers/{provider}.md lacks exactly one '## {string.Join("', '## ", missing)}'.");

        string[] steps = TableFirstColumn(sections.Single(section => section.Heading == "Tool map").Body);
        string[] missingSteps = ToolMapSteps.Except(steps, StringComparer.Ordinal).ToArray();
        Assert.True(missingSteps.Length == 0, $"providers/{provider}.md § Tool map lacks rows: {string.Join(", ", missingSteps)}");

        string[] sources = LinkTargets(sections.Single(section => section.Heading == "Sources").Body);
        Assert.Contains(sources, url => url.StartsWith("https://", StringComparison.Ordinal));

        if (provider == "github")
        {
            string[] scripts = LinkTargets(sections.Single(section => section.Heading == "Helper scripts").Body);
            Assert.Contains("../scripts/github-create-pr.sh", scripts);
            Assert.Contains("../scripts/github-create-pr.ps1", scripts);
        }
    }

    /// <summary>
    /// Prevents one host project's examples, configuration paths, and MCP server naming from
    /// returning to portable Squad source, where every host that installs it would inherit them.
    /// </summary>
    [Fact]
    public void CanonicalSquadSourceCarriesNoRetiredHostFacts()
    {
        List<string> findings = [];
        foreach (string scope in new[] { "skills", "agents", "standards" })
        {
            HostFactRule[] rules = HostFactRules.Where(rule => rule.Scopes.Contains(scope, StringComparer.Ordinal)).ToArray();
            foreach (string file in Directory.EnumerateFiles(Path.Combine(ProductRoot, scope), "*", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                string relativePath = Path.GetRelativePath(ProductRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                string[] lines = File.ReadAllLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    foreach (HostFactRule rule in rules)
                    {
                        Match match = Regex.Match(lines[index], rule.Pattern, rule.Options);
                        if (match.Success)
                        {
                            findings.Add($"{relativePath}:{index + 1}: '{match.Value}' ({rule.Reason})");
                        }
                    }
                }
            }
        }

        Assert.True(findings.Count == 0, "Host facts in canonical Squad source:\n" + string.Join("\n", findings));
    }

    private static string[] ExpectedResources(string skillName) => skillName switch
    {
        CombinedSkill => CombinedSkillResources,
        ReviewSkill => ReviewSkillResources,
        _ => throw new ArgumentOutOfRangeException(nameof(skillName), skillName, "Not a provider-aware skill.")
    };

    private static ISquadRenderer[] AllRenderers() =>
    [
        new CopilotRenderer(),
        new CursorRenderer(),
        new ClaudeRenderer(),
        new AntigravityRenderer(),
        new CodexRenderer(),
        new OpenCodeRenderer(),
        new KiloRenderer(),
        new PiRenderer(),
        new FactoryRenderer(),
        new WarpRenderer(),
        new ZCodeRenderer(),
        new DevinRenderer()
    ];

    private static void AssertSectionStates(Section[] sections, string heading, params string[] phrases)
    {
        Section[] matching = sections.Where(section => section.Heading == heading).ToArray();
        Assert.True(matching.Length == 1, $"Expected exactly one '## {heading}', found {matching.Length}.");

        // A soft line break inside a Markdown paragraph is a space, so wrapping must not hide a phrase.
        string prose = Regex.Replace(matching[0].Body, @"\s+", " ");
        foreach (string phrase in phrases)
        {
            Assert.True(
                prose.Contains(phrase, StringComparison.Ordinal),
                $"'## {heading}' does not state '{phrase}'.");
        }
    }

    /// <summary>Splits Markdown into level-2 sections, ignoring headings inside code.</summary>
    private static Section[] H2Sections(string markdown)
    {
        HeadingBlock[] headings = Markdown.Parse(markdown, Pipeline)
            .Descendants<HeadingBlock>()
            .Where(heading => heading.Level == 2)
            .ToArray();
        Section[] sections = new Section[headings.Length];
        for (int index = 0; index < headings.Length; index++)
        {
            int start = headings[index].Span.Start;
            int end = index + 1 < headings.Length ? headings[index + 1].Span.Start : markdown.Length;
            string heading = string.Concat(headings[index].Inline?
                .Descendants<LiteralInline>()
                .Select(literal => literal.Content.ToString()) ?? []);
            sections[index] = new Section(heading.Trim(), markdown[start..end]);
        }

        return sections;
    }

    private static string[] LinkTargets(string markdown) =>
        Markdown.Parse(markdown, Pipeline)
            .Descendants<LinkInline>()
            .Where(link => !link.IsImage && link.Url is not null)
            .Select(link => link.Url!)
            .ToArray();

    /// <summary>Returns every code span and every fenced or indented code line, trimmed.</summary>
    private static IEnumerable<string> CodeText(string markdown)
    {
        MarkdownDocument document = Markdown.Parse(markdown, Pipeline);
        foreach (CodeInline code in document.Descendants<CodeInline>())
        {
            yield return code.Content.Trim();
        }

        foreach (CodeBlock block in document.Descendants<CodeBlock>())
        {
            foreach (string line in block.Lines.ToString().Split('\n'))
            {
                yield return line.Trim();
            }
        }
    }

    private static string[] TableFirstColumn(string markdown) =>
        markdown.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith('|'))
            .Select(line => line.Split('|')[1].Trim())
            .Where(cell => cell.Length > 0 && !cell.All(character => character is '-' or ':'))
            .ToArray();

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private sealed record Section(string Heading, string Body);

    private sealed record HostFactRule(string Pattern, RegexOptions Options, string[] Scopes, string Reason);
}
