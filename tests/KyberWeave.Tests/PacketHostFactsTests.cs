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
        Assert.Contains("`.agent_scratchpad/T4/`", facts, StringComparison.Ordinal);
        Assert.Contains("<plan-index>: `handbook/plans/README.md`", facts, StringComparison.Ordinal);
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

    [Fact]
    public void ARuleSectionOverTheCapKeepsOnlyItsFirstLevelBulletsAndSaysSo()
    {
        string filler = new('x', PacketHostFacts.RuleSectionCap);
        Agents($"## Hard rules\n\n- One.\n  {filler}\n- Two.\n");

        string facts = PacketHostFacts.Render(_root, Config(), null, null);

        Assert.Contains("over the cap", facts, StringComparison.Ordinal);
        Assert.Contains("- One.", facts, StringComparison.Ordinal);
        Assert.Contains("- Two.", facts, StringComparison.Ordinal);
        Assert.DoesNotContain(filler, facts, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostWithNoRuleSectionGetsNoRulesBlock()
    {
        Agents("# Repo\n\n## Commands\n\n- build\n");

        Assert.DoesNotContain("Host rules", PacketHostFacts.Render(_root, Config(), null, null), StringComparison.Ordinal);
    }
}
