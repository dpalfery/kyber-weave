using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The task-gate hint after a project-scope deployment reads the project's host config through the configuration
/// repository, so a unit test can pin the declared-gates, none-declared and unreadable cases without a config file.
/// </summary>
public sealed class SquadTaskGateHintTests
{
    [Fact]
    public void AgentKeyedTaskGatesSuppressTheHint()
    {
        FakeKyberWeaveConfigRepository configs = Config("""
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
            """);

        Assert.True(SquadTaskGateHint.HasTaskGates(configs, "/project"));
    }

    [Fact]
    public void TechnologyKeyedTaskGatesSuppressTheHint()
    {
        FakeKyberWeaveConfigRepository configs = Config("""
            review:
              technology-gates:
                csharp:
                  - id: build
                    run: [dotnet, build]
            """);

        Assert.True(SquadTaskGateHint.HasTaskGates(configs, "/project"));
    }

    [Fact]
    public void AProjectWithNoConfigGatesKeepsTheHint()
    {
        Assert.False(SquadTaskGateHint.HasTaskGates(Config("review:\n  parallelism: 3\n"), "/project"));
    }

    [Fact]
    public void AnUnreadableConfigKeepsTheHintRatherThanFailingTheVerb()
    {
        Assert.False(SquadTaskGateHint.HasTaskGates(Config("review:\n  parallelism: not-a-number\n"), "/project"));
    }

    private static FakeKyberWeaveConfigRepository Config(string yaml)
    {
        FakeKyberWeaveConfigRepository configs = new();
        configs.WriteRepositoryConfig("/project", yaml);
        return configs;
    }
}
