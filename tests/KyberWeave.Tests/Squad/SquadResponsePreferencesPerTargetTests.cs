using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Acceptance tests for the deployment-time response-preferences injection contract across
/// every canonical Squad target. The real canonical source for each target is rendered first,
/// then the operator text is injected into the target's entry file(s) rather than into any
/// other rendered file.
/// </summary>
public sealed partial class SquadResponsePreferencesPerTargetTests
{
    private const string DistinctiveText = "operator response-preferences: keep the plan in lockstep with the current request";

    public static TheoryData<SquadTarget, string, string[]> EntryFilesByTarget =>
        new()
        {
            { SquadTarget.Codex, "conductor", [".codex/agents/conductor.toml"] },
            { SquadTarget.Codex, "bug-crusher", [".codex/agents/bug-crusher.toml"] },

            { SquadTarget.Cursor, "conductor", [".cursor/skills/conductor/SKILL.md"] },
            { SquadTarget.Cursor, "bug-crusher", [".cursor/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Claude, "conductor", [".claude/agents/conductor.md"] },
            { SquadTarget.Claude, "bug-crusher", [".claude/agents/bug-crusher.md"] },

            { SquadTarget.Copilot, "conductor", [".github/agents/conductor.agent.md"] },
            { SquadTarget.Copilot, "bug-crusher", [".github/agents/bug-crusher.agent.md"] },

            { SquadTarget.OpenCode, "conductor", [".opencode/agents/conductor.md"] },
            { SquadTarget.OpenCode, "bug-crusher", [".opencode/agents/bug-crusher.md"] },

            { SquadTarget.Kilo, "conductor", [".kilo/agents/conductor.md"] },
            { SquadTarget.Kilo, "bug-crusher", [".kilo/agents/bug-crusher.md"] },

            { SquadTarget.Antigravity, "conductor", [".agents/agents/conductor/agent.md"] },
            { SquadTarget.Antigravity, "bug-crusher", [".agents/agents/bug-crusher/agent.md"] },

            { SquadTarget.Factory, "conductor", [".factory/droids/conductor.md"] },
            { SquadTarget.Factory, "bug-crusher", [".factory/droids/bug-crusher.md"] },

            { SquadTarget.Warp, "conductor", [".warp/skills/conductor/SKILL.md", ".warp/skills/role-conductor/SKILL.md"] },
            { SquadTarget.Warp, "bug-crusher", [".warp/skills/bug-crusher/SKILL.md", ".warp/skills/role-bug-crusher/SKILL.md"] },

            { SquadTarget.Pi, "conductor", [".pi/skills/conductor/SKILL.md"] },
            { SquadTarget.Pi, "bug-crusher", [".pi/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.ZCode, "conductor", [".zcode/commands/conductor.md"] },
            { SquadTarget.ZCode, "bug-crusher", [".zcode/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.Devin, "conductor", [".devin/skills/conductor/SKILL.md"] },
            { SquadTarget.Devin, "bug-crusher", [".devin/skills/bug-crusher/SKILL.md"] },

            { SquadTarget.CommandCode, "conductor", [".commandcode/agents/conductor.md"] },
            { SquadTarget.CommandCode, "bug-crusher", [".commandcode/agents/bug-crusher.md"] },
        };

    [Theory]
    [MemberData(nameof(EntryFilesByTarget))]
    public async Task RealCanonicalTree_InjectsOperatorTextIntoExactlyTheTargetEntryFiles(
        SquadTarget target,
        string agentName,
        string[] expectedEntryFiles)
    {
        SquadRenderResult baseline = await TestSquadCorpus.GetProjectRenderAsync(target);

        SquadRenderResult injected = SquadResponsePreferencesInjector.Inject(baseline, DistinctiveText);

        HashSet<string> expectedSet = expectedEntryFiles.ToHashSet(StringComparer.Ordinal);

        foreach (SquadDeploymentFile file in injected.Files)
        {
            string content = Encoding.UTF8.GetString(file.Content.Span);
            bool hasBlock = ResponsePreferencesRegex().IsMatch(content);

            if (expectedSet.Contains(file.RelativePath))
            {
                Assert.True(hasBlock, $"Expected '{file.RelativePath}' to contain exactly one <response-preferences> block.");

                MatchCollection matches = ResponsePreferencesRegex().Matches(content);
                Assert.Single(matches);
                Assert.Contains(
                    $"<response-preferences>{DistinctiveText}</response-preferences>",
                    content,
                    StringComparison.Ordinal);
                continue;
            }

            Assert.False(hasBlock, $"Unexpected response-preferences block in '{file.RelativePath}' for target '{target}'.");
        }

        foreach (string expectedPath in expectedEntryFiles)
        {
            SquadDeploymentFile[] matches = injected.Files
                .Where(file => string.Equals(file.RelativePath, expectedPath, StringComparison.Ordinal))
                .ToArray();

            Assert.Single(matches);
            string content = Encoding.UTF8.GetString(matches[0].Content.Span);
            Assert.Contains(
                $"<response-preferences>{DistinctiveText}</response-preferences>",
                content,
                StringComparison.Ordinal);
        }

        int actualEntryCount = injected.Files.Count(file => expectedSet.Contains(file.RelativePath));
        Assert.True(
            actualEntryCount == expectedEntryFiles.Length,
            $"Expected {expectedEntryFiles.Length} response-preferences entry files for '{target}/{agentName}', found {actualEntryCount}.");
    }

    [GeneratedRegex("<response-preferences>.*?</response-preferences>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ResponsePreferencesRegex();
}
