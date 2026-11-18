using KyberWeave.Core.Squad.Briefs;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
///     Keeps the brief map true to the reference files it describes, and the install locations true to where
///     each harness puts them.
/// </summary>
public sealed class SquadBriefMapTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly string ProductRoot =
        Path.Combine(Corpus.Root, "products", "kyber-squad");

    /// <summary>
    ///     A renamed or removed reference file or heading fails here with the exact name to fix in
    ///     <c>src/KyberWeave.Domain/Squad/Briefs/Resources/briefs.yml</c>, instead of the brief silently
    ///     treating the new text as unmapped.
    /// </summary>
    [Fact]
    public void EveryFileAndHeadingTheMapNamesExistsInTheProductSource()
    {
        BriefMap map = BriefMapLoader.Load();
        List<string> problems = [];

        foreach (string agent in map.Agents)
        {
            string directory = SquadReferenceLocator.InSource(ProductRoot, agent);
            foreach (BriefFileRule rule in map.FilesOf(agent)!)
            {
                string path = CorpusPath(Path.Combine(directory, rule.File));
                if (!Corpus.Exists(path))
                {
                    problems.Add($"briefs.yml names file '{rule.File}' for agent '{agent}', but {path} does not exist.");
                    continue;
                }

                HashSet<string> present = MarkdownSections.Split(Corpus.ReadText(path))
                    .Where(section => section.Heading is not null)
                    .Select(section => MarkdownSections.Normalize(section.Heading!))
                    .ToHashSet(StringComparer.Ordinal);
                problems.AddRange(rule.Sections.Keys
                    .Where(heading => !present.Contains(MarkdownSections.Normalize(heading)))
                    .Select(heading => $"briefs.yml maps heading '{heading}' in {agent}/{rule.File}, but that file has no such heading."));
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Turns an absolute path inside the corpus into the corpus-relative path the seam reads by.</summary>
    private static string CorpusPath(string absolutePath) =>
        Path.GetRelativePath(Corpus.Root, absolutePath).Replace('\\', '/');

    /// <summary>The shipped reference files all reach a brief: none sits beside the mapped ones unlisted.</summary>
    [Fact]
    public void EveryReferenceFileOfAMappedAgentIsInTheMap()
    {
        BriefMap map = BriefMapLoader.Load();
        List<string> unlisted = [];

        foreach (string agent in map.Agents)
        {
            string[] mapped = [.. map.FilesOf(agent)!.Select(rule => rule.File)];
            unlisted.AddRange(Corpus.EnumerateFiles(
                    CorpusPath(SquadReferenceLocator.InSource(ProductRoot, agent)), recursive: false)
                .Select(file => Path.GetFileName(file))
                .Where(name => !mapped.Contains(name, StringComparer.Ordinal))
                .Select(name => $"{agent}/references/{name} is not in briefs.yml, so no brief carries it."));
        }

        Assert.True(unlisted.Count == 0, string.Join(Environment.NewLine, unlisted));
    }

    /// <summary>
    ///     The locator's candidate directories are checked against what a real render deploys, so a harness
    ///     that moves where it puts an agent's references fails here instead of making the brief look in the
    ///     wrong place. Entry-point agents are lowered onto skills by some harnesses, and the check covers both.
    /// </summary>
    [Theory]
    [InlineData("conductor")]
    [InlineData("architect")]
    public async Task EveryHarnessInstallsReferencesWhereTheLocatorLooks(string agent)
    {
        List<string> problems = [];
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            SquadRenderResult render = await TestSquadCorpus.GetProjectRenderAsync(target);
            string[] deployed = [.. render.Files
                .Select(file => file.RelativePath)
                .Where(path => path.Contains($"/{agent}/references/", StringComparison.Ordinal))
                .Select(path => path[..(path.LastIndexOf('/') + 1)])
                .Distinct(StringComparer.Ordinal)];
            string[] expected = [.. SquadReferenceLocator.Candidates(agent, target, ["."], null)
                .Select(directory => Path.GetRelativePath(".", directory).Replace('\\', '/') + "/")];

            if (deployed.Length == 0)
            {
                problems.Add($"{target}: the render deploys no reference files for '{agent}', so there is nothing to brief from.");
            }
            else if (!deployed.All(expected.Contains))
            {
                problems.Add($"{target}: the render deploys {string.Join(", ", deployed)}, but the locator only looks in {string.Join(", ", expected)}.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
