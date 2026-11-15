using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The host facts a task packet carries so a worker does not open the host AGENTS.md: Config Reg paths, the task
/// gates declared for the task's role, and the host's rule section copied, never parsed.
/// </summary>
public sealed class PacketHostFactsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kw-hostfacts-" + Guid.NewGuid().ToString("N"));

    public PacketHostFactsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static KyberWeaveConfig Config(string yaml = "ontology:\n  docs-root: handbook\n") => KyberWeaveConfigLoader.LoadFromYaml(yaml);

    private void Agents(string markdown) => File.WriteAllText(Path.Combine(_root, "AGENTS.md"), markdown);

    [Fact]
    public void TheSectionCarriesTheTaskScratchpadAndTheConfiguredPlanningPaths()
    {
        string facts = PacketHostFacts.Render(_root, Config(), "csharp-dev", "T4");

        Assert.StartsWith(PacketHostFacts.Heading, facts, StringComparison.Ordinal);
        Assert.Contains("`.agent_scratchpad/t4/`", facts, StringComparison.Ordinal);
        Assert.Contains("<plan-index>: `handbook/plans/README.md`", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSectionTellsTheWorkerToCommitItsTaskWithTheTaskIdAndNeverToPush()
    {
        string facts = PacketHostFacts.Render(_root, Config(), "csharp-dev", "T4");

        Assert.Contains("-m \"Task: T4\"", facts, StringComparison.Ordinal);
        Assert.Contains("return the sha as COMMIT", facts, StringComparison.Ordinal);
        Assert.Contains("never push", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRolesDeclaredTaskGatesAreListedWithTheirCommands()
    {
        KyberWeaveConfig config = Config("""
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    include: ["**/*.cs"]
                    run: [dotnet, build, "{changed}"]
            """);

        string facts = PacketHostFacts.Render(_root, config, "csharp-dev", "T1");
        string other = PacketHostFacts.Render(_root, config, "go-dev", "T1");

        Assert.Contains("- `build`: `dotnet build {changed}`", facts, StringComparison.Ordinal);
        Assert.Contains("Task gates for go-dev: none declared", other, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoAgentKnownTheSectionSaysHowToListTheWorkersOwnGates()
    {
        string facts = PacketHostFacts.Render(_root, Config(), null, "T1");

        Assert.Contains("--agent <your agent name>", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortRuleSectionIsCopiedVerbatimAndAHeadingInsideACodeFenceDoesNotEndIt()
    {
        Agents("# Repo\n\n## Non-negotiables\n\n- Never push to main.\n  Ask first.\n\n```bash\n# not a heading\n```\n\n- Keep ids.\n\n## Other\n\ntext\n");

        string facts = PacketHostFacts.Render(_root, Config(), null, null);

        Assert.Contains("verbatim from AGENTS.md § Non-negotiables", facts, StringComparison.Ordinal);
        Assert.Contains("  Ask first.", facts, StringComparison.Ordinal);
        Assert.Contains("- Keep ids.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("text", facts.Split("Non-negotiables")[1], StringComparison.Ordinal);
    }

    private string Render(out List<string> notes)
    {
        notes = [];
        return PacketHostFacts.Render(_root, Config(), null, null, notes);
    }

    [Fact]
    public void AnEmptyMisspelledRulesSectionBeforeTheRealOneDoesNotHideTheRealRules()
    {
        Agents("# Repo\n\n## Mandatory rules adhearents\n\nRead this file first.\n\n**CRITICAL**: ALL agents MUST:\nFollow the rule system in `docs/rules/`:\n\n## Non-negotiable rules\n\n- Never commit secrets.\n- No fallbacks.\n\n## Other\n\ntext\n");

        string facts = Render(out List<string> notes);

        Assert.Contains("verbatim from AGENTS.md § Non-negotiable rules", facts, StringComparison.Ordinal);
        Assert.Contains("- Never commit secrets.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("adhearents", facts, StringComparison.Ordinal);
        Assert.Single(notes);
        Assert.Contains("Mandatory rules adhearents", notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRulesSectionIsCopiedInDocumentOrder()
    {
        Agents("## Constraints\n\n- Alpha.\n\n## Commands\n\n- build\n\n## What agents must never do\n\n- Beta.\n\n## Security policies\n\n- Gamma.\n");

        string facts = Render(out _);

        Assert.True(facts.IndexOf("- Alpha.", StringComparison.Ordinal) < facts.IndexOf("- Beta.", StringComparison.Ordinal));
        Assert.True(facts.IndexOf("- Beta.", StringComparison.Ordinal) < facts.IndexOf("- Gamma.", StringComparison.Ordinal));
        Assert.DoesNotContain("- build", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void ASectionWithOnlyALeadInIsSkippedAndLoggedButNeverShownToTheWorker()
    {
        Agents("## Rules\n\nAll agents must follow these:\n\n## Guardrails\n\n- Keep it small.\n");

        string facts = Render(out List<string> notes);

        Assert.DoesNotContain("follow these", facts, StringComparison.Ordinal);
        Assert.Contains("- Keep it small.", facts, StringComparison.Ordinal);
        Assert.Contains("'Rules'", Assert.Single(notes), StringComparison.Ordinal);
        Assert.DoesNotContain("empty rules section", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedCodeGraphAndConfigRegBlocksAreNeverCopiedAsRules()
    {
        Agents("## Rules\n\n- Real rule.\n\n<!-- CODEGRAPH_START -->\n- codegraph block line\n<!-- CODEGRAPH_END -->\n\n<!-- KYBER_WEAVE_CONFIG_REG_START -->\n## Config Reg rules\n\n- config reg line\n<!-- KYBER_WEAVE_CONFIG_REG_END -->\n");

        string facts = Render(out _);

        Assert.Contains("- Real rule.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("codegraph block line", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("config reg line", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void RulesOverTheBudgetStopAtTheLastSectionThatFitsAndNameTheRest()
    {
        string filler = new('x', PacketHostFacts.RulesBudget - 100);
        Agents($"## Rules\n\n- One {filler}\n\n## Non-negotiables\n\n- Two {filler}\n\n## Guardrails\n\n- Three.\n");

        string facts = Render(out _);

        Assert.Contains("- One ", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("- Two ", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("- Three.", facts, StringComparison.Ordinal);
        Assert.Contains("Not copied here, over the 6000-character budget: AGENTS.md § Non-negotiables; § Guardrails.", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void AFirstSectionOverTheBudgetKeepsItsFirstLevelBulletsAndSaysSo()
    {
        string filler = new('x', PacketHostFacts.RulesBudget);
        Agents($"## Hard rules\n\n- One.\n  {filler}\n- Two.\n");

        string facts = Render(out _);

        Assert.Contains("first-level bullet lines", facts, StringComparison.Ordinal);
        Assert.Contains("- One.", facts, StringComparison.Ordinal);
        Assert.Contains("- Two.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain(filler, facts, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostWithNoRulesHeadingIsToldSoPlainly()
    {
        Agents("# Repo\n\n## Commands\n\n- build\n");

        string facts = Render(out List<string> notes);

        Assert.Contains("Host rules: no heading in AGENTS.md names rules", facts, StringComparison.Ordinal);
        Assert.Empty(notes);
    }

    [Fact]
    public void TheRulesFolderIsOnePointerLineToItsIndexAndItsTextIsNotCopied()
    {
        Agents("## Rules\n\n- Be kind.\n");
        Directory.CreateDirectory(Path.Combine(_root, "handbook", "rules"));
        File.WriteAllText(Path.Combine(_root, "handbook", "rules", "README.md"), "# Rules\n\n- Folder-only rule text.\n");

        string facts = Render(out _);

        Assert.Contains("Rules folder: `handbook/rules/README.md`", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("Folder-only rule text", facts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommitLineComesAfterTheHostRulesAndSaysWhatItApprovesAndWhatItDoesNot()
    {
        Agents("## Non-negotiables\n\n- Do not commit without explicit user approval.\n");

        string facts = Render(out _);

        Assert.True(facts.IndexOf("Do not commit without", StringComparison.Ordinal) < facts.IndexOf("Commit, for this run", StringComparison.Ordinal));
        Assert.Contains("never push", facts, StringComparison.Ordinal);
    }
}
