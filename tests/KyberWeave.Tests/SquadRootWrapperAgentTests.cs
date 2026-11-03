using System.Globalization;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the two primary shared identities — the <c>product-owner</c> and <c>bug-crusher</c> root
/// wrappers — across every declared target.
/// </summary>
/// <remarks>
/// A primary shared identity is the thin-wrapper shape: the agent body points at the same-name
/// canonical skill, which carries the role. The canonical skill is therefore the entry point and
/// must be emitted everywhere, exactly once, while a target that renders a primary through an
/// additional entry-point form must not emit a second skill at the same identity, and a target
/// that lowers a primary onto a skill or command must reuse the canonical skill rather than
/// emitting one of its own.
/// </remarks>
public sealed class SquadRootWrapperAgentTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly string[] RootWrapperIdentities = ["bug-crusher", "product-owner"];

    public static TheoryData<SquadTarget, string?, string> TargetProjections()
    {
        return new TheoryData<SquadTarget, string?, string>
        {
            // target, agent-file template (null when the target emits no agent principal), skill path
            { SquadTarget.Copilot, ".github/agents/{0}.agent.md", ".github/skills/{0}/SKILL.md" },
            { SquadTarget.Cursor, null, ".cursor/skills/{0}/SKILL.md" },
            { SquadTarget.Claude, ".claude/agents/{0}.md", ".claude/skills/{0}/SKILL.md" },
            { SquadTarget.Codex, ".codex/agents/{0}.toml", ".codex/skills/{0}/SKILL.md" },
            { SquadTarget.OpenCode, ".opencode/agents/{0}.md", ".opencode/skills/{0}/SKILL.md" },
            { SquadTarget.Kilo, ".kilo/agents/{0}.md", ".kilo/skills/{0}/SKILL.md" },
            { SquadTarget.Antigravity, ".agents/agents/{0}/agent.md", ".agents/skills/{0}/SKILL.md" },
            { SquadTarget.Warp, null, ".warp/skills/{0}/SKILL.md" },
            { SquadTarget.Factory, ".factory/droids/{0}.md", ".factory/skills/{0}/SKILL.md" },
            { SquadTarget.Pi, null, ".pi/skills/{0}/SKILL.md" },
            { SquadTarget.ZCode, null, ".zcode/skills/{0}/SKILL.md" },
            { SquadTarget.Devin, null, ".devin/skills/{0}/SKILL.md" },
            { SquadTarget.CommandCode, ".commandcode/agents/{0}.md", ".commandcode/skills/{0}/SKILL.md" }
        };
    }

    /// <summary>
    /// Every target emits exactly one canonical-skill projection per root-wrapper identity, with
    /// no <c>role-</c> duplicate, and the agent principal is present or absent exactly as the
    /// target's primary policy dictates.
    /// </summary>
    [Theory]
    [MemberData(nameof(TargetProjections))]
    public async Task EachTargetProjectsEachRootWrapperExactlyOnce(
        SquadTarget target,
        string? agentPathTemplate,
        string skillPathTemplate)
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string identity in RootWrapperIdentities)
        {
            SquadSkill skill = Assert.Single(source.Skills, s => s.Name == identity);
            string skillPath = string.Format(CultureInfo.InvariantCulture, skillPathTemplate, identity);
            string skillDirectory = skillPath[..^"/SKILL.md".Length];

            // The complete expected path set for this identity, built from source. Asserting the
            // whole set rather than a few members is deliberate: the projection defects this test
            // exists to catch are duplicated or leaked paths, and a count that only ever checks
            // presence would let an extra path through. Every assertion here reads the in-memory
            // render result, so it is independent of any temp-directory cleanup behaviour.
            HashSet<string> expectedPaths = new(StringComparer.Ordinal) { skillPath };
            foreach (SquadResource resource in skill.Resources)
            {
                expectedPaths.Add($"{skillDirectory}/{resource.RelativePath}");
            }

            if (agentPathTemplate is null)
            {
                // This target emits no agent principal for a primary; the canonical skill is the
                // only principal.
                Assert.DoesNotContain(
                    result.Files,
                    f => MentionsIdentity(f.RelativePath, identity) &&
                         f.RelativePath.EndsWith(".md", StringComparison.Ordinal) &&
                         !f.RelativePath.StartsWith($"{skillDirectory}/", StringComparison.Ordinal) &&
                         !f.RelativePath.StartsWith($".agents/agents/{identity}/", StringComparison.Ordinal) &&
                         !f.RelativePath.StartsWith($".devin/agents/{identity}/", StringComparison.Ordinal));
            }
            else
            {
                expectedPaths.Add(string.Format(CultureInfo.InvariantCulture, agentPathTemplate, identity));
            }

            string[] actualPaths = result.Files
                .Select(f => f.RelativePath)
                .Where(path => MentionsIdentity(path, identity))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(expectedPaths.Order(StringComparer.Ordinal), actualPaths);

            // No role-prefixed duplicate of a root wrapper on any target.
            Assert.DoesNotContain(
                result.Files,
                f => f.RelativePath.Contains($"role-{identity}", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> names <paramref name="identity"/> as a whole path
    /// segment or as the file stem. Segment matching keeps <c>bug-crusher</c> from matching
    /// <c>bug-crusher-investigator</c>, and the stem check covers the flat agent-file forms
    /// (<c>&lt;name&gt;.md</c>, <c>&lt;name&gt;.agent.md</c>, <c>&lt;name&gt;.toml</c>).
    /// </summary>
    private static bool MentionsIdentity(string path, string identity)
    {
        string[] segments = path.Split('/');
        if (segments.Any(segment => string.Equals(segment, identity, StringComparison.Ordinal)))
        {
            return true;
        }

        string fileName = segments[^1];
        int dot = fileName.IndexOf('.', StringComparison.Ordinal);
        string stem = dot >= 0 ? fileName[..dot] : fileName;
        return string.Equals(stem, identity, StringComparison.Ordinal);
    }

    /// <summary>
    /// The canonical skill is the role and the agent body only points at it, so the two bodies
    /// differ by design. This is the one case where a shared identity's bodies are allowed to
    /// diverge; a subagent shared identity still requires identical bodies.
    /// </summary>
    [Fact]
    public void RootWrappersArePrimarySharedIdentitiesWithThinWrapperBodies()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> shared = source.FallbackProfiles.Profiles["role-skill"].SharedIdentities
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(RootWrapperIdentities.Order(StringComparer.Ordinal), shared.Order(StringComparer.Ordinal));

        foreach (string identity in RootWrapperIdentities)
        {
            SquadAgent agent = Assert.Single(source.Agents, a => a.Name == identity);
            SquadSkill skill = Assert.Single(source.Skills, s => s.Name == identity);

            Assert.Equal(SquadInvocation.Primary, agent.Invocation);
            Assert.Equal("role-skill", agent.Fallback);

            // Compare the two bodies, not the agent's digest against the skill's text:
            // BodyDigest is Sha256(body) and InstructionBody is raw markdown, so they can
            // never be equal and NotEqual passes no matter what the bodies say.
            Assert.False(
                string.Equals(agent.InstructionBody, skill.InstructionBody, StringComparison.Ordinal),
                $"The '{identity}' agent body must differ from its canonical skill body: the skill "
                + "carries the role and the agent body is a thin wrapper pointing at it.");
            Assert.Contains($"`{identity}` skill", agent.InstructionBody, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Claude and Factory keep the native agent file and reuse the canonical skill as the entry
    /// point: they must not emit a second entry-point skill at the same identity.
    /// </summary>
    [Theory]
    [InlineData(SquadTarget.Claude)]
    [InlineData(SquadTarget.Factory)]
    public async Task DualEntryPointTargetsDoNotDoubleEmitTheSkill(SquadTarget target)
    {
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        string token = SquadTargetCatalog.GetToken(target);
        foreach (string identity in RootWrapperIdentities)
        {
            string skillPath =
                $"{token switch { "claude" => ".claude", "factory" => ".factory", _ => throw new InvalidOperationException() }}/skills/{identity}/SKILL.md";
            Assert.Single(result.Files, f => f.RelativePath == skillPath);

            SquadDegradationRecord fallback = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == identity && d.Code == "role-skill-fallback");
            Assert.Equal(identity, fallback.OutputIdentity);
            Assert.Contains("canonical skill", fallback.Details, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The fail-closed throw is retained for a real collision: a canonical skill occupying a
    /// primary identity that is NOT a declared shared identity cannot be resolved by
    /// role-prefixing on a native target.
    /// </summary>
    [Fact]
    public async Task NonSharedPrimarySkillCollisionStillFailsClosed()
    {
        using PiPrimaryIdentityCollisionFixture fixture = PiPrimaryIdentityCollisionFixture.Create("conductor");

        ClaudeRenderer renderer = new();
        SquadRenderValidationException exception = await Assert.ThrowsAsync<SquadRenderValidationException>(() =>
            renderer.RenderAsync(new SquadRenderRequest(
                fixture.ProductRoot,
                [SquadTarget.Claude],
                SquadDeploymentScope.Project)));

        // Assert the collision-specific wording, not just the agent name: any
        // validation error that happens to mention "conductor" would satisfy a
        // bare Contains, including an unrelated failure from the same fixture.
        Assert.Contains(
            $"Cannot render primary agent 'conductor' as a Claude entry-point skill",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("already occupies that identity", exception.Message, StringComparison.Ordinal);
        Assert.Contains("fail-closed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The degradation record names the skill path that was actually emitted. Under Global scope
    /// the physical root is already <c>~/.claude</c>, so the emitted path is the bare
    /// <c>skills/&lt;name&gt;/SKILL.md</c> form; a message hardcoding the project-scope
    /// <c>.claude/skills/...</c> literal would point at a path that was never written.
    /// </summary>
    [Theory]
    [InlineData(SquadDeploymentScope.Project, ".claude/skills")]
    [InlineData(SquadDeploymentScope.Global, "skills")]
    public async Task ReusedCanonicalSkillDetailNamesTheEmittedPath(
        SquadDeploymentScope scope,
        string expectedDirectory)
    {
        SquadRenderResult result = await TestSquadCorpus.GetDefaultRenderAsync(SquadTarget.Claude, scope);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string identity in RootWrapperIdentities)
        {
            SquadDegradationRecord reused = Assert.Single(
                result.Degradations,
                d => d.CanonicalIdentity == identity && d.Code == "role-skill-fallback");
            string expectedPath = $"{expectedDirectory}/{identity}/SKILL.md";

            // Assert the backtick-delimited path, not a bare substring: "skills/x" is a
            // substring of ".claude/skills/x", so a bare Contains passes for the wrong
            // directory and the hardcoded-literal regression would slip through.
            Assert.Contains($"`{expectedPath}`", reused.Details, StringComparison.Ordinal);
            Assert.Single(
                result.Files,
                f => f.RelativePath == $"{expectedDirectory}/{identity}/SKILL.md");
        }
    }

    /// <summary>
    /// The bundle roster and the agent directory stay in step: every canonical agent, including
    /// the new root wrapper, is listed in the full bundle.
    /// </summary>
    [Fact]
    public void FullBundleRosterMatchesTheAgentDirectory()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;

        Assert.Contains("bug-crusher", source.Bundle.AgentNames);
        Assert.Equal(
            source.Agents.Select(agent => agent.Name).Order(StringComparer.Ordinal),
            source.Bundle.AgentNames.Order(StringComparer.Ordinal));
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
