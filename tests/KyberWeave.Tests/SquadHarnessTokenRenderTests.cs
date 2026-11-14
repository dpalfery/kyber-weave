using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Every shipped harness render replaces the canonical <c>{{harness}}</c> placeholder with that
///     harness's own catalogue token, because a run-start step cannot name the harness it is running on
///     unless the render tells it.
/// </summary>
/// <remarks>
///     <para>
///         The probe is a body the renderer has never seen substituted: the canonical corpus with one extra
///         line appended to the conductor's instruction body. Appending rather than replacing keeps every
///         other byte of the corpus in place, so a render that fails here fails on the placeholder alone.
///     </para>
///     <para>
///         The token an assertion expects is <see cref="SquadTargetCatalog.GetToken" />, and the rows come
///         from <see cref="SquadCommandComposition.CreateRenderers" />, so a renderer added later is held to
///         this contract the day it ships and no list of thirteen names lives in this file.
///     </para>
/// </remarks>
public sealed class SquadHarnessTokenRenderTests
{
    /// <summary>The placeholder the canonical corpus carries where a harness name belongs.</summary>
    private const string Placeholder = "{{harness}}";

    /// <summary>
    ///     The probe line the assertion looks for once substituted. The literal around the placeholder is
    ///     what makes the search specific: a bare token such as <c>kilo</c> appears in file paths.
    /// </summary>
    private const string ProbeLabel = "Carried harness token: ";

    private const string ProbeLine = ProbeLabel + Placeholder;

    private const string ProbeAgentName = "conductor";

    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    public static TheoryData<SquadTarget> ShippedTargets()
    {
        TheoryData<SquadTarget> rows = [];
        foreach (SquadTarget target in SquadCommandComposition.CreateRenderers()
                     .SelectMany(renderer => renderer.SupportedTargets))
            rows.Add(target);

        return rows;
    }

    /// <summary>
    ///     One render per shipped harness: no rendered file keeps the placeholder, the render carries the
    ///     token the catalogue names for that harness, and the untouched canonical corpus leaves none
    ///     behind either — so a canonical file that gains the token can never ship the braces.
    /// </summary>
    /// <param name="target">The harness being rendered.</param>
    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public async Task EachHarnessRenderReplacesThePlaceholderWithItsOwnToken(SquadTarget target)
    {
        ISquadRenderer renderer = RendererFor(target);
        string token = SquadTargetCatalog.GetToken(target);

        SquadRenderResult probe = await renderer.RenderAsync(
            new SquadRenderRequest(
                ProductRoot,
                [target],
                SquadDeploymentScope.Project,
                PreloadedSource: SourceWithProbe()));
        Assert.True(probe.Success, string.Join("; ", probe.Errors));

        string[] unresolved = probe.Files
            .Where(file => Text(file).Contains(Placeholder, StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToArray();
        Assert.True(
            unresolved.Length == 0,
            $"The {token} render still carries {Placeholder} in: {string.Join(", ", unresolved)}.");

        string[] carrying = probe.Files
            .Where(file => Text(file).Contains(ProbeLabel + token, StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToArray();
        Assert.True(
            carrying.Length > 0,
            $"No file in the {token} render carries '{ProbeLabel}{token}'. The placeholder was removed " +
            "without a token taking its place, or it was substituted with another harness's token.");

        SquadRenderResult corpus = await renderer.RenderAsync(
            new SquadRenderRequest(
                ProductRoot,
                [target],
                SquadDeploymentScope.Project,
                PreloadedSource: TestSquadCorpus.CanonicalSource));
        Assert.True(corpus.Success, string.Join("; ", corpus.Errors));

        string[] corpusLeftovers = corpus.Files
            .Where(file => Text(file).Contains(Placeholder, StringComparison.Ordinal))
            .Select(file => file.RelativePath)
            .ToArray();
        Assert.True(
            corpusLeftovers.Length == 0,
            $"The {token} render of the canonical corpus carries an unresolved {Placeholder} in: " +
            $"{string.Join(", ", corpusLeftovers)}.");
    }

    /// <summary>
    ///     One render per shipped harness answers <c>{{subagents-dispatch}}</c> with <c>yes</c> exactly where
    ///     the catalogue says a dispatched agent can dispatch agents of its own, and leaves no braces behind.
    /// </summary>
    /// <param name="target">The harness being rendered.</param>
    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public async Task EachHarnessRenderAnswersWhetherSubagentsDispatch(SquadTarget target)
    {
        const string placeholder = "{{subagents-dispatch}}";
        const string label = "Carried subagent dispatch: ";
        string expected = SquadTargetCatalog.GetSubagentDispatch(target) == SquadSubagentDispatch.Nested ? "yes" : "no";

        SquadRenderResult probe = await RendererFor(target).RenderAsync(
            new SquadRenderRequest(
                ProductRoot,
                [target],
                SquadDeploymentScope.Project,
                PreloadedSource: SourceWithProbe(label + placeholder)));
        Assert.True(probe.Success, string.Join("; ", probe.Errors));

        Assert.DoesNotContain(probe.Files, file => Text(file).Contains(placeholder, StringComparison.Ordinal));
        Assert.Contains(probe.Files, file => Text(file).Contains(label + expected, StringComparison.Ordinal));
    }

    /// <summary>The harnesses verified to nest are the ones the catalogue marks nested; every other one takes the safe path.</summary>
    [Fact]
    public void OnlyVerifiedHarnessesAreMarkedAsNesting()
    {
        SquadTarget[] nested = SquadTargetCatalog.All
            .Where(target => SquadTargetCatalog.GetSubagentDispatch(target) == SquadSubagentDispatch.Nested)
            .ToArray();

        Assert.Equal([SquadTarget.Cursor, SquadTarget.Claude, SquadTarget.Copilot, SquadTarget.Pi], nested);
    }

    /// <summary>
    ///     The canonical corpus with one probe line appended to one agent's body. Appending leaves the
    ///     shared-identity body comparison and every other rendered byte untouched.
    /// </summary>
    private static SquadSource SourceWithProbe(string probeLine = ProbeLine)
    {
        SquadSource canonical = TestSquadCorpus.CanonicalSource;
        SquadAgent[] agents = canonical.Agents
            .Select(agent => agent.Name == ProbeAgentName
                ? agent with { InstructionBody = agent.InstructionBody + "\n\n" + probeLine + "\n" }
                : agent)
            .ToArray();

        return canonical with { Agents = agents };
    }

    private static ISquadRenderer RendererFor(SquadTarget target)
    {
        return SquadCommandComposition.CreateRenderers().Single(renderer => renderer.SupportedTargets.Contains(target));
    }

    private static string Text(SquadDeploymentFile file)
    {
        return Encoding.UTF8.GetString(file.Content.Span);
    }
}
