using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Acceptance tests for the deployment-time response-preferences injection contract across
/// every canonical Squad target. Each target keeps exactly the canonical entry carriers and
/// every injected block is carried through unchanged without affecting derived or unrelated
/// files.
/// </summary>
public sealed partial class SquadResponsePreferencesPerTargetTests
{
    private const string DistinctiveText = "sentinel-operator-preferences 7f3a: differs from the canonical placeholder";

    public static TheoryData<SquadTarget, string, string[]> EntryFilesByTarget =>
        new()
        {
            { SquadTarget.Codex, "conductor", [".codex/agents/conductor.toml", ".codex/skills/conductor/SKILL.md"] },
            { SquadTarget.Codex, "bug-crusher", [".codex/agents/bug-crusher.toml", ".codex/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Cursor, "conductor", [".cursor/skills/conductor/SKILL.md"] },
            { SquadTarget.Cursor, "bug-crusher", [".cursor/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Claude, "conductor", [".claude/agents/conductor.md", ".claude/skills/conductor/SKILL.md"] },
            { SquadTarget.Claude, "bug-crusher", [".claude/agents/bug-crusher.md", ".claude/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Copilot, "conductor", [".github/agents/conductor.agent.md"] },
            { SquadTarget.Copilot, "bug-crusher", [".github/agents/bug-crusher.agent.md", ".github/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.OpenCode, "conductor", [".opencode/agents/conductor.md"] },
            { SquadTarget.OpenCode, "bug-crusher", [".opencode/agents/bug-crusher.md", ".opencode/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Kilo, "conductor", [".kilo/agents/conductor.md"] },
            { SquadTarget.Kilo, "bug-crusher", [".kilo/agents/bug-crusher.md", ".kilo/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Antigravity, "conductor", [".agents/agents/conductor/agent.md"] },
            { SquadTarget.Antigravity, "bug-crusher", [".agents/agents/bug-crusher/agent.md", ".agents/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Factory, "conductor", [".factory/droids/conductor.md", ".factory/skills/conductor/SKILL.md"] },
            { SquadTarget.Factory, "bug-crusher", [".factory/droids/bug-crusher.md", ".factory/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Warp, "conductor", [".warp/skills/conductor/SKILL.md"] },
            { SquadTarget.Warp, "bug-crusher", [".warp/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Pi, "conductor", [".pi/skills/conductor/SKILL.md"] },
            { SquadTarget.Pi, "bug-crusher", [".pi/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.ZCode, "conductor", [".zcode/commands/conductor.md"] },
            { SquadTarget.ZCode, "bug-crusher", [".zcode/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Devin, "conductor", [".devin/skills/conductor/SKILL.md"] },
            { SquadTarget.Devin, "bug-crusher", [".devin/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.CommandCode, "conductor", [".commandcode/agents/conductor.md", ".commandcode/skills/conductor/SKILL.md"] },
            { SquadTarget.CommandCode, "bug-crusher", [".commandcode/agents/bug-crusher.md", ".commandcode/skills/bug-crusher/SKILL.md"] },
        };

    private static readonly Dictionary<SquadTarget, HashSet<string>> TargetEntryFiles = new()
    {
        [SquadTarget.Codex] = [
            ".codex/agents/conductor.toml",
            ".codex/skills/conductor/SKILL.md",
            ".codex/agents/bug-crusher.toml",
            ".codex/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Cursor] = [
            ".cursor/skills/conductor/SKILL.md",
            ".cursor/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Claude] = [
            ".claude/agents/conductor.md",
            ".claude/skills/conductor/SKILL.md",
            ".claude/agents/bug-crusher.md",
            ".claude/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Copilot] = [
            ".github/agents/conductor.agent.md",
            ".github/agents/bug-crusher.agent.md",
            ".github/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.OpenCode] = [
            ".opencode/agents/conductor.md",
            ".opencode/agents/bug-crusher.md",
            ".opencode/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Kilo] = [
            ".kilo/agents/conductor.md",
            ".kilo/agents/bug-crusher.md",
            ".kilo/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Antigravity] = [
            ".agents/agents/conductor/agent.md",
            ".agents/agents/bug-crusher/agent.md",
            ".agents/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Factory] = [
            ".factory/droids/conductor.md",
            ".factory/skills/conductor/SKILL.md",
            ".factory/droids/bug-crusher.md",
            ".factory/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Warp] = [
            ".warp/skills/conductor/SKILL.md",
            ".warp/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Pi] = [
            ".pi/skills/conductor/SKILL.md",
            ".pi/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.ZCode] = [
            ".zcode/commands/conductor.md",
            ".zcode/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.Devin] = [
            ".devin/skills/conductor/SKILL.md",
            ".devin/skills/bug-crusher/SKILL.md",
        ],
        [SquadTarget.CommandCode] = [
            ".commandcode/agents/conductor.md",
            ".commandcode/skills/conductor/SKILL.md",
            ".commandcode/agents/bug-crusher.md",
            ".commandcode/skills/bug-crusher/SKILL.md",
        ],
    };

    private static HashSet<string> GetTargetUnionSet(SquadTarget target)
    {
        return new HashSet<string>(TargetEntryFiles[target], StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EntryFilesByTarget))]
    public async Task RealCanonicalTree_CarriesTheTargetUnionOfResponsePreferenceEntryFiles(
        SquadTarget target,
        string agentName,
        string[] expectedEntryFiles)
    {
        SquadRenderResult baseline = await TestSquadCorpus.GetProjectRenderAsync(target);

        HashSet<string> identitySet = expectedEntryFiles.ToHashSet(StringComparer.Ordinal);
        HashSet<string> targetUnionSet = GetTargetUnionSet(target);

        // The raw render carries the canonical placeholder, never the sentinel, so the sentinel check can
        // only pass once the injection has actually run.
        foreach (SquadDeploymentFile file in baseline.Files)
        {
            Assert.DoesNotContain(DistinctiveText, Encoding.UTF8.GetString(file.Content.Span), StringComparison.Ordinal);
        }

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(baseline, DistinctiveText);
        AssertEntryFilesForMarker(target, injected, targetUnionSet, identitySet, DistinctiveText, agentName);

        string secondMarker = $"distinct marker for {target}/{agentName} v2";
        SquadRenderResult rerendered = SquadResponsePreferencesInjector.Inject(baseline, secondMarker);
        AssertEntryFilesForMarker(target, rerendered, targetUnionSet, identitySet, secondMarker, agentName);
    }

    private static void AssertEntryFilesForMarker(
        SquadTarget target,
        SquadRenderResult render,
        HashSet<string> targetUnionSet,
        HashSet<string> identitySet,
        string marker,
        string agentName)
    {
        HashSet<string> actualSet = [];

        foreach (SquadDeploymentFile file in render.Files)
        {
            string content = Encoding.UTF8.GetString(file.Content.Span);
            bool hasBlock = ResponsePreferencesRegex().IsMatch(content);

            if (hasBlock)
            {
                actualSet.Add(file.RelativePath);
            }

            if (targetUnionSet.Contains(file.RelativePath))
            {
                Assert.True(hasBlock, $"Expected '{file.RelativePath}' to contain exactly one <response-preferences> block for '{target}/{agentName}'.");

                MatchCollection matches = ResponsePreferencesRegex().Matches(content);
                Assert.Single(matches);
                Assert.Contains(
                    $"<response-preferences>{marker}</response-preferences>",
                    content,
                    StringComparison.Ordinal);
                continue;
            }

            Assert.False(hasBlock, $"Unexpected response-preferences block in '{file.RelativePath}' for target '{target}/{agentName}'.");
        }

        Assert.Equal(targetUnionSet, actualSet);
        Assert.True(identitySet.IsSubsetOf(actualSet), $"Every file for identity '{agentName}' on target '{target}' must be present in the target union's block-bearing set.");
    }

    [GeneratedRegex("<response-preferences>.*?</response-preferences>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ResponsePreferencesRegex();
}
