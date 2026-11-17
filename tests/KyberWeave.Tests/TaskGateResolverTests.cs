using KyberWeave.Application.Review;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A task's gates resolve from one place: the agent's own <c>review.task-gates</c> entry, then the task
/// technology's <c>review.technology-gates</c> entry, then the entry of the technology agent the shipped map
/// names, then the parent technology's entry. The council's <c>review.gates</c> are not part of it.
/// </summary>
public sealed class TaskGateResolverTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    private static KyberWeaveConfig Config(string yaml) => KyberWeaveConfigLoader.LoadFromYaml(yaml);

    private void Standard(string technology, string frontmatter, string fileName = "")
    {
        string folder = Path.Combine(_root.Path, "docs", "standards", technology);
        Directory.CreateDirectory(folder);
        string name = fileName.Length == 0 ? technology + ".standards.md" : fileName;
        File.WriteAllText(Path.Combine(folder, name), $"---\n{frontmatter}\n---\n\n# {technology}\n");
    }

    [Fact]
    public void AnAgentsOwnEntryWinsOverTheTechnologyEntry()
    {
        ReviewConfig review = Config("""
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
              technology-gates:
                csharp:
                  - id: test
                    run: [dotnet, test]
            """).Review;

        TaskGateResolution resolved = TaskGateResolver.Resolve(review, "csharp-dev", "csharp");

        Assert.Equal(["build"], resolved.Gates.Select(gate => gate.Id));
        Assert.Equal(TaskGateSource.Agent, resolved.Source);
        Assert.Null(resolved.Refusal);
    }

    [Fact]
    public void ATechnologyEntryIsUsedForATaskWhoseTechnologyTheHostDeclares()
    {
        ReviewConfig review = Config("""
            review:
              technology-gates:
                csharp:
                  - id: build
                    run: [dotnet, build]
                  - id: test
                    run: [dotnet, test]
            """).Review;

        TaskGateResolution resolved = TaskGateResolver.Resolve(review, "dev-medium", "csharp");

        Assert.Equal(["build", "test"], resolved.Gates.Select(gate => gate.Id));
        Assert.Equal(TaskGateSource.Technology, resolved.Source);
        Assert.Equal("csharp", resolved.EntryKey);
        Assert.Equal("csharp", resolved.Technology);
        Assert.Null(resolved.Refusal);
    }

    [Fact]
    public void WithoutATechnologyEntryTheMappedTechnologysAgentEntryIsUsed()
    {
        ReviewConfig review = Config("""
            review:
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
            """).Review;

        TaskGateResolution resolved = TaskGateResolver.Resolve(review, "dev-medium", "csharp");

        Assert.Equal(["build"], resolved.Gates.Select(gate => gate.Id));
        Assert.Equal(TaskGateSource.MappedAgent, resolved.Source);
        Assert.Equal("csharp-dev", resolved.EntryKey);
    }

    [Fact]
    public void WithNeitherTheParentsGatesAreUsed()
    {
        Standard("react", "builds-on: typescript");
        KyberWeaveConfig config = Config("""
            ontology:
              docs-root: docs
            review:
              task-gates:
                typescript-dev:
                  - id: typecheck
                    run: [npx, tsc]
            """);

        string? parent = TaskGateResolver.ParentTechnology(_root.Path, config, "react");
        TaskGateResolution resolved = TaskGateResolver.Resolve(config.Review, "dev-medium", "react", parent);

        Assert.Equal("typescript", parent);
        Assert.Equal(["typecheck"], resolved.Gates.Select(gate => gate.Id));
        Assert.Equal(TaskGateSource.MappedAgent, resolved.Source);
        Assert.Equal("typescript", resolved.Technology);
    }

    [Fact]
    public void WithNothingAtAllAndATechnologyNamedTheResolutionIsRefused()
    {
        ReviewConfig review = Config("ontology:\n  docs-root: docs\n").Review;

        TaskGateResolution resolved = TaskGateResolver.Resolve(review, "dev-medium", "sql-server");

        Assert.Empty(resolved.Gates);
        Assert.Equal(TaskGateSource.None, resolved.Source);
        Assert.NotNull(resolved.Refusal);
        Assert.Contains("sql-server", resolved.Refusal, StringComparison.Ordinal);
        Assert.Contains("review.technology-gates.sql-server", resolved.Refusal, StringComparison.Ordinal);
        Assert.Contains("review.task-gates", resolved.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentWithNoEntryAndNoTechnologyResolvesNothingWithoutARefusal()
    {
        ReviewConfig review = Config("ontology:\n  docs-root: docs\n").Review;

        TaskGateResolution resolved = TaskGateResolver.Resolve(review, "dev-medium");

        Assert.Empty(resolved.Gates);
        Assert.Equal(TaskGateSource.None, resolved.Source);
        Assert.Null(resolved.Refusal);
    }

    [Fact]
    public void TheCouncilsGatesAreNotPartOfTheResolution()
    {
        KyberWeaveConfig config = Config("""
            review:
              gates:
                - id: test
                  run: [dotnet, test]
              task-gates:
                csharp-dev:
                  - id: build
                    run: [dotnet, build]
            """);

        TaskGateResolution resolved = TaskGateResolver.Resolve(config.Review, "csharp-dev", "csharp");

        Assert.Equal(["build"], resolved.Gates.Select(gate => gate.Id));
        Assert.Equal(["test"], config.Review.Gates.Select(gate => gate.Id));
    }

    [Fact]
    public void TheParentIsReadFromTheStandardFile()
    {
        Standard("tauri", "builds-on: rust");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        Assert.Equal("rust", TaskGateResolver.ParentTechnology(_root.Path, config, "tauri"));
    }

    [Fact]
    public void TheParentIsReadFromALegacyFolderWhoseStandardIsStillReadme()
    {
        Standard("react", "builds-on: typescript", "README.md");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        string expected = Path.Combine(_root.Path, "docs", "standards", "react", "README.md");
        Assert.True(File.Exists(expected), expected);

        Assert.Equal("typescript", TaskGateResolver.ParentTechnology(_root.Path, config, "react"));
    }

    [Fact]
    public void ATechnologyWithNoStandardOfItsOwnHasNoParent()
    {
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        Assert.Null(TaskGateResolver.ParentTechnology(_root.Path, config, "csharp"));
        Assert.Null(TaskGateResolver.ParentTechnology(_root.Path, config, null));
    }

    [Fact]
    public void ATechnologyNameThatIsNotALookupNameIsNeverReadFromDisk()
    {
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        Assert.Null(TaskGateResolver.ParentTechnology(_root.Path, config, "../escape"));
    }

    /// <summary>
    /// Gate resolution runs while a plan is being read, so a standard it cannot parse must name no parent rather
    /// than crash the resolution: the mapped agent's entry is the fallback, as it is for a technology with no
    /// standard.
    /// </summary>
    [Fact]
    public void AStandardWhoseFrontmatterIsMalformedNamesNoParentInsteadOfCrashing()
    {
        Standard("react", "builds-on: [unclosed");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        Assert.Null(TaskGateResolver.ParentTechnology(_root.Path, config, "react"));
    }
    [Fact]
    public void TechnologyGatesAreKeyedByTechnologyAndReadInDeclarationOrder()
    {
        IReadOnlyDictionary<string, IReadOnlyList<ReviewTaskGate>> gates = Config("""
            review:
              technology-gates:
                csharp:
                  - id: build
                    run: [dotnet, build]
                  - id: test
                    run: [dotnet, test]
                go:
                  - id: vet
                    run: [go, vet, ./...]
            """).Review.TechnologyGates;

        Assert.Equal(["csharp", "go"], gates.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["build", "test"], gates["csharp"].Select(gate => gate.Id));
        Assert.Equal(["vet"], gates["go"].Select(gate => gate.Id));
    }

    [Fact]
    public void AnAbsentTechnologyGatesSectionDeclaresNone()
    {
        Assert.Empty(Config("ontology:\n  docs-root: docs\n").Review.TechnologyGates);
    }

    [Fact]
    public void ATechnologyKeyThatIsNotALookupNameIsRefused()
    {
        Exception refused = Assert.ThrowsAny<Exception>(() => Config("""
            review:
              technology-gates:
                CSharp:
                  - id: build
                    run: [dotnet, build]
            """));

        Assert.Contains("review.technology-gates", refused.Message, StringComparison.Ordinal);
        Assert.Contains("CSharp", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATechnologyEntryWithNoGatesIsRefused()
    {
        Exception refused = Assert.ThrowsAny<Exception>(() => Config("""
            review:
              technology-gates:
                csharp: []
            """));

        Assert.Contains("review.technology-gates 'csharp'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("no gates", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyInsideATechnologyGateIsReportedAndTheConfigStillLoads()
    {
        KyberWeaveConfig config = Config("""
            review:
              technology-gates:
                csharp:
                  - id: build
                    runs: [dotnet, build]
                    run: [dotnet, build]
            """);

        string warning = Assert.Single(config.Review.Warnings);
        Assert.Contains("review.technology-gates 'csharp' gate 'build'", warning, StringComparison.Ordinal);
        Assert.Contains("'runs'", warning, StringComparison.Ordinal);
    }
}
