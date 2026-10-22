using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Rust and Go specialists (#287): they render on every approved target, the
/// conductor can route to them, their triggers keep non-Tauri Rust away from <c>tauri-dev</c>,
/// and they run on a mid-tier model rather than the fast tier.
/// </summary>
/// <remarks>
/// The conductor routes by matching a change against each delegate's description, so the
/// description wording <em>is</em> the routing table. These assertions pin the trigger phrases a
/// router needs to tell <c>src-tauri</c> Rust from any other Rust; loosening one silently sends
/// work to the wrong agent.
/// </remarks>
public sealed class SquadRustGoSpecialistTests
{
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
    /// A fresh install on any approved target delivers both specialists with their standard
    /// lookup and their skill's references. A target that dropped either would leave the
    /// conductor delegating to an agent that does not exist there.
    /// </summary>
    [Theory]
    [MemberData(nameof(ApprovedTargets))]
    public async Task EveryApprovedTargetRendersTheRustAndGoSpecialists(SquadTarget target)
    {
        SquadRenderResult result = await AllRenderers().RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [target],
            SquadDeploymentScope.Project));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        AssertSpecialistRendered(result, "rust-dev", "<rust-coding-standard>", "errors-and-boundaries");
        AssertSpecialistRendered(result, "go-dev", "<go-coding-standard>", "errors-and-context");
    }

    [Fact]
    public void ConductorDelegatesToTheRustGoAndTauriSpecialists()
    {
        SquadAgent conductor = LoadAgent("conductor");

        Assert.Contains("rust-dev", conductor.DelegatesTo);
        Assert.Contains("go-dev", conductor.DelegatesTo);
        Assert.Contains("tauri-dev", conductor.DelegatesTo);
    }

    /// <summary>
    /// A non-Tauri <c>.rs</c> change matches <c>rust-dev</c>, and <c>rust-dev</c> hands
    /// <c>src-tauri</c> back to <c>tauri-dev</c> while <c>tauri-dev</c> hands other Rust to
    /// <c>rust-dev</c>. Each side names the other so neither description matches both.
    /// </summary>
    [Fact]
    public void RustTriggersSplitNonTauriRustFromSrcTauri()
    {
        string rust = LoadAgent("rust-dev").Description;
        Assert.Contains(".rs file", rust, StringComparison.Ordinal);
        Assert.Contains("Cargo.toml", rust, StringComparison.Ordinal);
        Assert.Contains("build.rs", rust, StringComparison.Ordinal);
        Assert.Contains("outside a Tauri app", rust, StringComparison.Ordinal);
        Assert.Contains("Do not use for src-tauri", rust, StringComparison.Ordinal);
        Assert.Contains("(tauri-dev)", rust, StringComparison.Ordinal);
        Assert.Contains("(test-dev)", rust, StringComparison.Ordinal);

        string tauri = LoadAgent("tauri-dev").Description;
        Assert.Contains("Use when the change is under src-tauri", tauri, StringComparison.Ordinal);
        Assert.Contains("Do not use for Rust outside a Tauri app", tauri, StringComparison.Ordinal);
        Assert.Contains("(rust-dev)", tauri, StringComparison.Ordinal);
    }

    [Fact]
    public void GoTriggersCoverSourceAndModuleFilesButNotTests()
    {
        string go = LoadAgent("go-dev").Description;

        Assert.Contains(".go file", go, StringComparison.Ordinal);
        Assert.Contains("go.mod", go, StringComparison.Ordinal);
        Assert.Contains("go.sum", go, StringComparison.Ordinal);
        Assert.Contains("Do not use for _test.go", go, StringComparison.Ordinal);
        Assert.Contains("(test-dev)", go, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rust and Go test authorship stays with <c>test-dev</c>, so its trigger must name the
    /// runners and the file shapes the two specialists decline.
    /// </summary>
    [Fact]
    public void TestDevTriggerCoversCargoAndGoTests()
    {
        string testDev = LoadAgent("test-dev").Description;

        Assert.Contains("cargo test/nextest", testDev, StringComparison.Ordinal);
        Assert.Contains("go test", testDev, StringComparison.Ordinal);
        Assert.Contains("#[cfg(test)]", testDev, StringComparison.Ordinal);
        Assert.Contains("_test.go", testDev, StringComparison.Ordinal);
    }

    /// <summary>
    /// Implementation specialists map to the mid tier. The <c>general</c> profile resolves to a
    /// Sonnet-class model on Claude; the <c>fast</c> profile is Haiku, which #287 rules out.
    /// </summary>
    [Theory]
    [InlineData("rust-dev")]
    [InlineData("go-dev")]
    public void SpecialistUsesTheGeneralModelProfile(string agentName)
    {
        SquadSource source = SquadSourceLoader.Load(ProductRoot);
        SquadAgent agent = Assert.Single(source.Agents, a => a.Name == agentName);

        Assert.Equal("general", agent.ModelProfile);
        string claudeModel = source.ModelProfiles.Profiles["general"].HarnessModels["claude"];
        Assert.Contains("sonnet", claudeModel, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSpecialistRendered(
        SquadRenderResult result,
        string identity,
        string standardToken,
        string referenceStem)
    {
        SquadDeploymentFile[] principals = result.Files
            .Where(f => f.RelativePath.Contains(identity, StringComparison.Ordinal))
            .ToArray();

        Assert.True(principals.Length > 0, $"No rendered file carries the '{identity}' identity.");
        Assert.Contains(
            principals,
            f => Encoding.UTF8.GetString(f.Content.Span).Contains(standardToken, StringComparison.Ordinal));
        Assert.Contains(
            principals,
            f => f.RelativePath.Contains(referenceStem, StringComparison.Ordinal));
    }

    private static SquadAgent LoadAgent(string name) =>
        Assert.Single(SquadSourceLoader.Load(ProductRoot).Agents, a => a.Name == name);

    private static SquadRendererRegistry AllRenderers() => new(
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
        new CommandCodeRenderer(),
    ]);
}
