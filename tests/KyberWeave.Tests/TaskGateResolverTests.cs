using KyberWeave.Application.Review;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A task's gates resolve from one place: the agent's own <c>review.task-gates</c> entry, then the task
/// technology's <c>review.technology-gates</c> entry, then the entry of the technology agent the shipped map
/// names, then the parent technology's entry. The council's <c>review.gates</c> are not part of it.
/// </summary>
public sealed class TaskGateResolverTests
{
    private const string Root = "/repo";

    private readonly InMemoryFileSystem _files = new();

    private static KyberWeaveConfig Config(string yaml) => KyberWeaveConfigAssembler.FromYaml(yaml);

    private void Standard(string technology, string frontmatter, string fileName = "")
    {
        string folder = Path.Combine(Root, "docs", "standards", technology);
        _files.CreateDirectory(folder);
        string name = fileName.Length == 0 ? technology + ".standards.md" : fileName;
        _files.WriteAllText(Path.Combine(folder, name), $"---\n{frontmatter}\n---\n\n# {technology}\n");
    }

    private void FailReadOf(string path, Exception failure) =>
        _files.FailWhen(operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText) && operation.Path == path, failure);

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

        string? parent = TaskGateResolver.ParentTechnology(_files, Root, config, "react");
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

        Assert.Equal("rust", TaskGateResolver.ParentTechnology(_files, Root, config, "tauri"));
    }

    [Fact]
    public void TheParentIsReadFromALegacyFolderWhoseStandardIsStillReadme()
    {
        Standard("react", "builds-on: typescript", "README.md");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        string expected = Path.Combine(Root, "docs", "standards", "react", "README.md");
        Assert.True(_files.FileExists(expected), expected);

        Assert.Equal("typescript", TaskGateResolver.ParentTechnology(_files, Root, config, "react"));
    }

    [Fact]
    public void ATechnologyWithNoStandardOfItsOwnHasNoParent()
    {
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        Assert.Null(TaskGateResolver.ParentTechnology(_files, Root, config, "csharp"));
        Assert.Null(TaskGateResolver.ParentTechnology(_files, Root, config, null));
    }

    [Fact]
    public void ATechnologyNameThatIsNotALookupNameIsNeverReadFromDisk()
    {
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");

        Assert.Null(TaskGateResolver.ParentTechnology(_files, Root, config, "../escape"));
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

        Assert.Null(TaskGateResolver.ParentTechnology(_files, Root, config, "react"));
    }

    /// <summary>
    /// A malformed standard names no parent, but a standard this process may not read is a broken
    /// checkout rather than an absent parent: it must throw, so an operator repairs the file instead of a
    /// task resolving against the wrong gates.
    /// </summary>
    [Fact]
    public void AStandardTheProcessMayNotReadThrowsInsteadOfNamingNoParent()
    {
        Standard("react", "builds-on: dotnet");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");
        string path = Path.Combine(Root, "docs", "standards", "react", "react.standards.md");

        FailReadOf(path, new UnauthorizedAccessException($"Access to the path '{path}' is denied."));

        Assert.Throws<UnauthorizedAccessException>(
            () => TaskGateResolver.ParentTechnology(_files, Root, config, "react"));
    }

    /// <summary>
    /// The caller that must keep going asks for the parent or none: the unreadable standard names no parent
    /// and the reason comes back instead of a throw, so gate resolution can fall back and say why.
    /// </summary>
    [Fact]
    public void AnUnreadableStandardNamesNoParentForACallerThatCollectsTheProblem()
    {
        Standard("react", "builds-on: dotnet");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");
        string path = Path.Combine(Root, "docs", "standards", "react", "react.standards.md");

        FailReadOf(path, new UnauthorizedAccessException($"Access to the path '{path}' is denied."));

        List<string> problems = [];
        Assert.Null(TaskGateResolver.ParentTechnologyOrNone(_files, Root, config, "react", problems));
        string problem = Assert.Single(problems);
        Assert.Contains("react.standards.md", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A standard that exists but fails to read for an I/O reason (here, another handle holds it exclusively)
    /// is a present-but-unusable standard, not an absent parent: it must throw rather than name no parent.
    /// </summary>
    [Fact]
    public void AStandardThatExistsButFailsToReadThrowsInsteadOfNamingNoParent()
    {
        Standard("react", "builds-on: dotnet");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");
        string path = Path.Combine(Root, "docs", "standards", "react", "react.standards.md");

        FailReadOf(path, new IOException($"The process cannot access the file '{path}' because another handle holds it."));

        Assert.ThrowsAny<IOException>(() => TaskGateResolver.ParentTechnology(_files, Root, config, "react"));
    }

    /// <summary>
    /// The caller that must keep going reports any read failure of a present standard, not only a permission
    /// failure, so an I/O failure is never silently treated as an absent parent.
    /// </summary>
    [Fact]
    public void AStandardThatFailsToReadForAnIoReasonIsReportedToACallerThatCollectsTheProblem()
    {
        Standard("react", "builds-on: dotnet");
        KyberWeaveConfig config = Config("ontology:\n  docs-root: docs\n");
        string path = Path.Combine(Root, "docs", "standards", "react", "react.standards.md");

        FailReadOf(path, new IOException($"The process cannot access the file '{path}' because another handle holds it."));

        List<string> problems = [];
        Assert.Null(TaskGateResolver.ParentTechnologyOrNone(_files, Root, config, "react", problems));
        string problem = Assert.Single(problems);
        Assert.Contains("react.standards.md", problem, StringComparison.Ordinal);
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
