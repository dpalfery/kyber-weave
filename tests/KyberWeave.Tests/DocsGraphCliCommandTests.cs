using System.Text.Json;
using KyberWeave.Cli.Commands.Docs;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>Pins managed-glossary contribution at the public docs export-graph command boundary.</summary>
public sealed class DocsGraphCliCommandTests : IDisposable
{
    private readonly TempDirectory _repository = new();
    private readonly TempDirectory _output = new();

    [Fact]
    public void Execute_ManagedGlossary_ExportsApprovedKnowledgeOnlyAndPreservesDocuments()
    {
        WriteRepository();
        using var codeGraph = new CodeGraphFixtureDb();
        codeGraph.IndexSymbol("Game.Run", "src/Game.cs", 10);
        var codeGraphDirectory = Path.Combine(_repository.Path, ".codegraph");
        Directory.CreateDirectory(codeGraphDirectory);
        File.Copy(codeGraph.DatabasePath, Path.Combine(codeGraphDirectory, "codegraph.db"));

        var execution = ProcessConsoleCapture.Run(() => new DocsExportGraphCommand().Execute(
            null!,
            new DocsExportGraphSettings { Path = _repository.Path, Out = _output.Path }));
        var exitCode = execution.Result;
        Assert.Equal(0, exitCode);

        var nodes = ReadJsonLines(Path.Combine(_output.Path, "nodes.jsonl"));
        var edges = ReadJsonLines(Path.Combine(_output.Path, "edges.jsonl"));
        var allOutput = File.ReadAllText(Path.Combine(_output.Path, "nodes.jsonl"))
            + File.ReadAllText(Path.Combine(_output.Path, "edges.jsonl"));
        Assert.Contains(nodes, node => IsNode(node, "doc:reference/gameplay", "Document"));
        Assert.Contains(nodes, node => IsNode(node, "term:loop", "Term"));
        Assert.Contains(nodes, node => IsNode(node, "sense:loop-gameplay", "Sense"));
        Assert.Contains(nodes, node => IsNode(node, "term:gameplay-loop", "Term"));
        Assert.Contains(edges, edge => IsEdge(edge, "HAS_SENSE", "term:loop", "sense:loop-gameplay"));
        Assert.Contains(edges, edge => IsEdge(edge, "ALIAS_OF", "term:gameplay-loop", "sense:loop-gameplay"));
        Assert.Contains(edges, edge => IsEdge(edge, "SCOPED_TO", "sense:loop-gameplay", "component:Gameplay"));
        Assert.Contains(edges, edge => IsEdge(edge, "SCOPED_TO", "sense:loop-gameplay", "id-Game.Run"));
        Assert.Contains(edges, edge => IsEdge(edge, "EVIDENCED_BY", "sense:loop-gameplay", "claim-gameplay"));
        Assert.DoesNotContain("loop-proposed", allOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("loop-rejected", allOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("agent loop", allOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy loop", allOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_InvalidGlossary_ReturnsOperationalFailureInsteadOfThrowing()
    {
        WriteRepository();
        Write("docs/glossary.md", """
            ---
            id: reference/glossary
            title: Glossary
            doc-type: reference
            status: current
            owner: Gameplay maintainers
            last-reviewed: 2026-08-12
            ---

            # Glossary

            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | CURRENT | The gameplay update cycle. | component:Gameplay | gameplay loop |
            """);
        using var codeGraph = new CodeGraphFixtureDb();
        codeGraph.IndexSymbol("Game.Run", "src/Game.cs", 10);
        var codeGraphDirectory = Path.Combine(_repository.Path, ".codegraph");
        Directory.CreateDirectory(codeGraphDirectory);
        File.Copy(codeGraph.DatabasePath, Path.Combine(codeGraphDirectory, "codegraph.db"));

        var execution = ProcessConsoleCapture.Run(() => new DocsExportGraphCommand().Execute(
            null!,
            new DocsExportGraphSettings { Path = _repository.Path, Out = _output.Path, Format = "json" }));

        Assert.Equal(1, execution.Result);
        Assert.Contains("KW-DOC-GLOSSARY-001", execution.Output, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _output.Dispose();
        _repository.Dispose();
    }

    private void WriteRepository()
    {
        Write(".kyber-weave/kyber-weave.yml", """
            ontology:
              docs-root: docs
            docs-analysis:
              glossary-path: docs/glossary.md
            """);
        Write("docs/catalog.md", """
            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | Gameplay | Application | `src/Game` | [README](x) | [docs](y) | Gameplay maintainers | 2026-08-01 | Current |
            """);
        Write("docs/gameplay.md", """
            ---
            id: reference/gameplay
            title: Gameplay
            doc-type: reference
            status: current
            component: Gameplay
            owner: Gameplay maintainers
            last-reviewed: 2026-08-12
            code-refs:
              - Game.Run
            ---

            # Gameplay

            The gameplay loop updates the world.
            """);
        Write("docs/glossary.md", """
            ---
            id: reference/glossary
            title: Glossary
            doc-type: reference
            status: current
            owner: Gameplay maintainers
            last-reviewed: 2026-08-12
            ---

            # Glossary

            ## loop

            | Sense ID | Status | Definition | Scope | Aliases |
            |---|---|---|---|---|
            | loop-gameplay | approved | The gameplay update cycle. | component:Gameplay; code-ref:Game.Run | gameplay loop |
            | loop-proposed | proposed |  | component:Gameplay | agent loop |
            | loop-rejected | rejected | A retired cycle. | component:Gameplay | legacy loop |

            <!-- kyber-weave:glossary-evidence:start sense="loop-gameplay" -->
            - claim-gameplay
            <!-- kyber-weave:glossary-evidence:end -->
            """);
        Directory.CreateDirectory(Path.Combine(_repository.Path, "src", "Game"));
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_repository.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static JsonElement[] ReadJsonLines(string path) =>
        File.ReadAllLines(path)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static bool IsNode(JsonElement node, string id, string label) =>
        node.GetProperty("type").GetString() == "node"
        && node.GetProperty("id").GetString() == id
        && node.GetProperty("label").GetString() == label;

    private static bool IsEdge(JsonElement edge, string label, string from, string to) =>
        edge.GetProperty("type").GetString() == "edge"
        && edge.GetProperty("label").GetString() == label
        && edge.GetProperty("from").GetString() == from
        && edge.GetProperty("to").GetString() == to;
}
