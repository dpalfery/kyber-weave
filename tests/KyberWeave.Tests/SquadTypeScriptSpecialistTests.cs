using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the TypeScript specialist (#237): it renders on every approved target, the conductor
/// delegates to it, its trigger names the file shapes and hand-off agents that split Node
/// TypeScript from React UI and from tests, and it runs on a mid-tier model rather than the fast tier.
/// </summary>
/// <remarks>
/// Before this agent existed, non-test <c>.ts</c> matched no delegate, so conductors sent it to a
/// general-purpose agent or to <c>react-dev</c>, whose trigger is <c>.tsx</c>/<c>.jsx</c>. The
/// description wording is the routing table; these assertions pin the file shapes and agent names
/// that keep <c>.ts</c>, <c>.tsx</c>, and <c>.test.ts</c> each matching exactly one delegate, and
/// the "do not use for" routing rules are graded from the prose rule catalog.
/// </remarks>
public sealed class SquadTypeScriptSpecialistTests
{
    /// <summary>Every reference the <c>typescript-dev</c> skill ships, so none can drop from a render.</summary>
    private static readonly string[] ReferenceStems =
        ["types-and-absent-values", "data-access", "errors-and-async", "esm-and-gates"];

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    public static TheoryData<SquadTarget> ApprovedTargets()
    {
        TheoryData<SquadTarget> data = [];
        foreach (SquadTarget target in SquadTargetCatalog.All)
        {
            data.Add(target);
        }

        return data;
    }

    /// <summary>
    /// A fresh install on any approved target delivers the specialist with its standard lookup and
    /// all four of its skill's references. A target that dropped it would leave the conductor delegating to an
    /// agent that does not exist there.
    /// </summary>
    [Theory]
    [MemberData(nameof(ApprovedTargets))]
    public async Task EveryApprovedTargetRendersTheTypeScriptSpecialist(SquadTarget target)
    {
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile[] principals = result.Files
            .Where(f => f.RelativePath.Contains("typescript-dev", StringComparison.Ordinal))
            .ToArray();

        Assert.True(principals.Length > 0, "No rendered file carries the 'typescript-dev' identity.");
        Assert.Contains(
            principals,
            f => Encoding.UTF8.GetString(f.Content.Span)
                .Contains("<typescript-coding-standard>", StringComparison.Ordinal));
        Assert.All(
            ReferenceStems,
            stem => Assert.Contains(
                principals,
                f => f.RelativePath.Contains(stem, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The conductor lists the specialist and both of its neighbours as delegates. The routing
    /// rule in its body is graded from the prose rule catalog.
    /// </summary>
    [Fact]
    public void ConductorDelegatesToTheTypeScriptReactAndTestSpecialists()
    {
        SquadAgent conductor = LoadAgent("conductor");

        Assert.Contains("typescript-dev", conductor.DelegatesTo);
        Assert.Contains("react-dev", conductor.DelegatesTo);
        Assert.Contains("test-dev", conductor.DelegatesTo);
    }

    /// <summary>
    /// <c>typescript-dev</c> takes non-test <c>.ts</c> outside a React UI and names both
    /// neighbours it hands work to; <c>react-dev</c> hands Node TypeScript back. Each side names
    /// the other so neither description matches the same file.
    /// </summary>
    [Fact]
    public void TypeScriptTriggersSplitNodeCodeFromReactUiAndTests()
    {
        string typescript = LoadAgent("typescript-dev").Description;
        Assert.Contains("(react-dev)", typescript, StringComparison.Ordinal);
        Assert.Contains("(github-devops)", typescript, StringComparison.Ordinal);

        string react = LoadAgent("react-dev").Description;
        Assert.Contains(".tsx or .jsx file", react, StringComparison.Ordinal);
        Assert.Contains("(typescript-dev)", react, StringComparison.Ordinal);

        string testDev = LoadAgent("test-dev").Description;
        Assert.Contains(".test.ts or .spec.ts file", testDev, StringComparison.Ordinal);
    }

    /// <summary>
    /// The standard template carries its lookup token and the <c>Absent is not zero</c> section.
    /// The other defect-class rules that motivated the agent (N+1 queries, unbounded and wide
    /// reads, floating promises) are graded from the prose rule catalog.
    /// </summary>
    [Fact]
    public void StandardTemplateCarriesItsLookupTokenAndTheAbsentIsNotZeroSection()
    {
        string standard = File.ReadAllText(
            Path.Combine(ProductRoot, "standards", "typescript", "README.md"));

        Assert.Contains("`<typescript-coding-standard>`", standard, StringComparison.Ordinal);
        Assert.Contains("## Absent is not zero", standard, StringComparison.Ordinal);
    }

    /// <summary>
    /// Implementation specialists map to the mid tier, like <c>rust-dev</c> and <c>go-dev</c>. The
    /// <c>general</c> profile resolves to a Sonnet-class model on Claude and maps every harness the
    /// fast tier does.
    /// </summary>
    [Fact]
    public void SpecialistUsesTheGeneralModelProfile()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent agent = Assert.Single(source.Agents, a => a.Name == "typescript-dev");

        Assert.Equal("general", agent.ModelProfile);
        IReadOnlyDictionary<string, string> general = source.ModelProfiles.Profiles["general"].HarnessModels;
        Assert.Contains("sonnet", general["claude"], StringComparison.OrdinalIgnoreCase);
        Assert.All(
            source.ModelProfiles.Profiles["fast"].HarnessModels.Keys,
            harness => Assert.True(general.ContainsKey(harness), $"general has no model for '{harness}'."));
    }

    private static SquadAgent LoadAgent(string name)
    {
        return Assert.Single(TestSquadCorpus.CanonicalSource.Agents, a => a.Name == name);
    }

    private static SquadRendererRegistry AllRenderers()
    {
        return new SquadRendererRegistry(
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
            new DevinRenderer(),
            new CommandCodeRenderer()
        ]);
    }
}
