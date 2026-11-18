using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using KyberWeave.Tests.Corpus;

namespace KyberWeave.Tests;

/// <summary>
/// A worker's rendered body names the directory its skills were installed to, so a small model that
/// has to read a skill as a file opens that path instead of globbing the filesystem and finding
/// another checkout's copy. The expected root is derived from where the render itself puts a skill,
/// never typed out here.
/// </summary>
public sealed class SquadSkillsRootLineTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string BaselinePath = "shared/worker-baseline.md";
    private const string SampleWorker = "dev-medium";
    private const string SampleSkill = "task-gate";

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private static readonly Regex SkillsLine = new(
        @"it is at `(?<root>[^`]+)/<name>/SKILL\.md`",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    public static TheoryData<SquadTarget, SquadDeploymentScope> TargetsAndScopes()
    {
        TheoryData<SquadTarget, SquadDeploymentScope> data = [];
        foreach (SquadTarget target in SquadHostDefaults.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets))
        {
            data.Add(target, SquadDeploymentScope.Project);
            data.Add(target, SquadDeploymentScope.Global);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TargetsAndScopes))]
    public async Task TheWorkerBodyNamesTheDirectoryItsSkillsAreInstalledTo(
        SquadTarget target,
        SquadDeploymentScope scope)
    {
        SquadGlobalRoots roots = new(_ => null, Home());
        SquadRenderResult result = await RenderAsync(target, scope, roots);

        string expected = ExpectedRoot(result, scope, roots, target);
        string body = CarrierBody(result);

        Match line = SkillsLine.Match(body);
        Assert.True(line.Success, $"The {target} {scope} worker body carries no skills-location line.");
        Assert.Equal(expected, line.Groups["root"].Value);
        Assert.DoesNotContain(SquadWorkerBaselineInjector.SkillsRootPlaceholder, body, StringComparison.Ordinal);
        Assert.Equal(1, Regex.Count(body, "it is at `", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)));

        if (scope == SquadDeploymentScope.Global)
        {
            Assert.StartsWith("~/", line.Groups["root"].Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AGlobalRootOutsideTheHomeDirectoryIsWrittenAbsolute()
    {
        string custom = Path.Combine(_files.Root, "custom-copilot-home");
        SquadGlobalRoots roots = new(name => name == "COPILOT_HOME" ? custom : null, Home());
        SquadRenderResult result = await RenderAsync(SquadTarget.Copilot, SquadDeploymentScope.Global, roots);

        Match line = SkillsLine.Match(CarrierBody(result));

        Assert.True(line.Success);
        Assert.Equal(ExpectedRoot(result, SquadDeploymentScope.Global, roots, SquadTarget.Copilot), line.Groups["root"].Value);
        Assert.DoesNotContain("~", line.Groups["root"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGlobalRenderWithNoResolverDropsThePathLineRatherThanGuessOne()
    {
        SquadRenderResult result = await RenderAsync(SquadTarget.Claude, SquadDeploymentScope.Global, null);

        string body = CarrierBody(result);

        Assert.DoesNotContain(SquadWorkerBaselineInjector.SkillsRootPlaceholder, body, StringComparison.Ordinal);
        Assert.False(SkillsLine.IsMatch(body), "A render that cannot know the skills root must not name one.");
        Assert.Contains("codegraph_explore", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingNoRootRemovesOnlyTheParagraphThatCarriesThePlaceholder()
    {
        string block = $"<worker-baseline>\nfirst\n\nroot {SquadWorkerBaselineInjector.SkillsRootPlaceholder}/x\n\nlast\n</worker-baseline>";

        string? bound = SquadWorkerBaselineInjector.BindSkillsRoot(block, null);

        Assert.Equal("<worker-baseline>\nfirst\n\nlast\n</worker-baseline>", bound);
        Assert.Equal(
            "<worker-baseline>\nfirst\n\nroot .claude/skills/x\n\nlast\n</worker-baseline>",
            SquadWorkerBaselineInjector.BindSkillsRoot(block, ".claude/skills"));
    }

    private string Home() => Path.Combine(_files.Root, "home");

    private string SourceRoot() => Path.Combine(_files.Root, "products", "kyber-squad");

    private Task<SquadRenderResult> RenderAsync(
        SquadTarget target,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? roots)
    {
        string sourceRoot = SourceRoot();
        _files.CreateDirectory(Path.Combine(sourceRoot, "shared"));
        _files.WriteAllText(Path.Combine(sourceRoot, "shared", "worker-baseline.md"), Corpus.ReadText($"products/kyber-squad/{BaselinePath}"));
        SquadRendererRegistry registry = new(SquadHostDefaults.CreateRenderers(), new InMemorySquadServices(_files).RenderInputReader());
        SquadRenderRequest request = new(
            sourceRoot,
            [target],
            scope,
            GlobalRoots: roots,
            PreloadedSource: TestSquadCorpus.CanonicalSource);

        return registry.RenderAsync(request);
    }

    /// <summary>
    /// Where the render puts a skill, joined onto the global root the installer resolves for the
    /// target, then written home-relative the way a worker reads it.
    /// </summary>
    private static string ExpectedRoot(
        SquadRenderResult result,
        SquadDeploymentScope scope,
        SquadGlobalRoots roots,
        SquadTarget target)
    {
        string suffix = $"/{SampleSkill}/SKILL.md";
        string skillPath = Assert.Single(
            result.Files.Select(file => file.RelativePath.Replace('\\', '/')),
            path => path.EndsWith(suffix, StringComparison.Ordinal) && !path.Contains("/role-", StringComparison.Ordinal));
        string relativeRoot = skillPath[..^suffix.Length];
        if (scope == SquadDeploymentScope.Project)
        {
            return relativeRoot;
        }

        string absolute = Path.Combine(roots.ResolveGlobalRoot(target), relativeRoot).Replace('\\', '/');
        string home = roots.HomeDirectory!.Replace('\\', '/').TrimEnd('/');
        return absolute.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + absolute[home.Length..] : absolute;
    }

    private static string CarrierBody(SquadRenderResult result)
    {
        return Encoding.UTF8.GetString(Assert.Single(
            result.Files,
            file => file.RelativePath.Contains(SampleWorker, StringComparison.Ordinal)
                && Encoding.UTF8.GetString(file.Content.Span).Contains("<worker-baseline", StringComparison.Ordinal))
            .Content.Span);
    }
}
