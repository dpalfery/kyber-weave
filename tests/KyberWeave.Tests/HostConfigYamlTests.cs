using KyberWeave.Core.Docs.Scaffolding;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies text-preserving modifications to host configuration YAML files.
/// </summary>
public sealed class HostConfigYamlTests
{
    /// <summary>
    /// When ontology: exists without technologies:, WithTechnologies adds technologies:
    /// with proper block indentation.
    /// </summary>
    [Fact]
    public void AddsTechnologiesBlockWhenOntologyKeyHasNoTechnologies()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["csharp", "test"]);

        Assert.Contains("technologies:", result, StringComparison.Ordinal);
        Assert.Contains("  - 'csharp'", result, StringComparison.Ordinal);
        Assert.Contains("  - 'test'", result, StringComparison.Ordinal);
        Assert.Contains("docs-root: docs", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// When ontology: is missing completely, WithTechnologies creates an ontology block
    /// with technologies.
    /// </summary>
    [Fact]
    public void CreatesOntologyBlockWhenOntologyKeyIsMissing()
    {
        const string yaml =
            """
            # Custom host configuration
            harness:
              profiles:
                claude:
                  directory-name: .agents/agents
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["csharp", "react"]);

        Assert.Contains("ontology:", result, StringComparison.Ordinal);
        Assert.Contains("technologies:", result, StringComparison.Ordinal);
        Assert.Contains("- 'csharp'", result, StringComparison.Ordinal);
        Assert.Contains("- 'react'", result, StringComparison.Ordinal);
        Assert.Contains("# Custom host configuration", result, StringComparison.Ordinal);
        Assert.Contains("directory-name: .agents/agents", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// When technologies: already exists with a block sequence, new items are appended
    /// without duplicating existing entries.
    /// </summary>
    [Fact]
    public void MergesNewTechnologiesIntoExistingBlockSequenceWithoutDuplicates()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs
              technologies:
                - csharp
                - react
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["react", "pulumi", "azure"]);

        Assert.Contains("- csharp", result, StringComparison.Ordinal);
        Assert.Contains("- react", result, StringComparison.Ordinal);
        Assert.Contains("- 'pulumi'", result, StringComparison.Ordinal);
        Assert.Contains("- 'azure'", result, StringComparison.Ordinal);

        // Ensure react is not duplicated
        int firstIndex = result.IndexOf("react", StringComparison.Ordinal);
        int secondIndex = result.IndexOf("react", firstIndex + 1, StringComparison.Ordinal);
        Assert.Equal(-1, secondIndex);
    }

    /// <summary>
    /// Block sequence merging preserves technology names with commas, hashes, or quotes when parsed.
    /// </summary>
    [Fact]
    public void MergesBlockSequenceWithSpecialCharactersAndPreservesThemWhenParsed()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs
              technologies:
                - csharp
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["c#,c++", "f#", "tech#with,comma"]);

        Assert.Contains("- 'c#,c++'", result, StringComparison.Ordinal);
        Assert.Contains("- 'f#'", result, StringComparison.Ordinal);
        Assert.Contains("- 'tech#with,comma'", result, StringComparison.Ordinal);

        YamlStream stream = new YamlStream();
        using StringReader reader = new StringReader(result);
        stream.Load(reader);
        YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
        YamlMappingNode ontology = (YamlMappingNode)root.Children[new YamlScalarNode("ontology")];
        YamlSequenceNode techs = (YamlSequenceNode)ontology.Children[new YamlScalarNode("technologies")];
        List<string> parsed = techs.Children.Cast<YamlScalarNode>().Select(s => s.Value!).ToList();

        Assert.Equal(["csharp", "c#,c++", "f#", "tech#with,comma"], parsed);
    }

    /// <summary>
    /// When technologies: is written as a flow sequence, items are merged without duplicates.
    /// </summary>
    [Fact]
    public void MergesNewTechnologiesIntoExistingFlowSequenceWithoutDuplicates()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs
              technologies: [csharp, react]
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["react", "pulumi"]);

        Assert.Contains("csharp", result, StringComparison.Ordinal);
        Assert.Contains("react", result, StringComparison.Ordinal);
        Assert.Contains("'pulumi'", result, StringComparison.Ordinal);

        int firstIndex = result.IndexOf("react", StringComparison.Ordinal);
        int secondIndex = result.IndexOf("react", firstIndex + 1, StringComparison.Ordinal);
        Assert.Equal(-1, secondIndex);
    }

    /// <summary>
    /// Flow sequence merging preserves technology names with commas, hashes, or quotes when parsed.
    /// </summary>
    [Fact]
    public void MergesFlowSequenceWithSpecialCharactersAndPreservesThemWhenParsed()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs
              technologies: [csharp]
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["c#,c++", "f#", "tech#with,comma"]);

        Assert.Contains("'c#,c++'", result, StringComparison.Ordinal);
        Assert.Contains("'f#'", result, StringComparison.Ordinal);
        Assert.Contains("'tech#with,comma'", result, StringComparison.Ordinal);

        YamlStream stream = new YamlStream();
        using StringReader reader = new StringReader(result);
        stream.Load(reader);
        YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
        YamlMappingNode ontology = (YamlMappingNode)root.Children[new YamlScalarNode("ontology")];
        YamlSequenceNode techs = (YamlSequenceNode)ontology.Children[new YamlScalarNode("technologies")];
        List<string> parsed = techs.Children.Cast<YamlScalarNode>().Select(s => s.Value!).ToList();

        Assert.Equal(["csharp", "c#,c++", "f#", "tech#with,comma"], parsed);
    }

    /// <summary>
    /// Existing comments, keys, formatting, and inline comments are preserved byte-for-byte.
    /// </summary>
    [Fact]
    public void PreservesExistingCommentsAndFormattingWhenAddingTechnologies()
    {
        const string yaml =
            """
            # Hand-maintained host configuration
            ontology:
              # docs root comment
              docs-root: 'docs'  # keep this comment
              catalog: docs/catalog.md

            harness:
              profiles:
                claude:
                  directory-name: .agents/agents # agent dir
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["csharp", "sql"]);

        Assert.Contains("# Hand-maintained host configuration", result, StringComparison.Ordinal);
        Assert.Contains("# docs root comment", result, StringComparison.Ordinal);
        Assert.Contains("docs-root: 'docs'  # keep this comment", result, StringComparison.Ordinal);
        Assert.Contains("catalog: docs/catalog.md", result, StringComparison.Ordinal);
        Assert.Contains("directory-name: .agents/agents # agent dir", result, StringComparison.Ordinal);
        Assert.Contains("technologies:", result, StringComparison.Ordinal);
        Assert.Contains("- 'csharp'", result, StringComparison.Ordinal);
        Assert.Contains("- 'sql'", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scalar <c>technologies: csharp</c> must not be treated as an empty block sequence.
    /// Merging would insert <c>- item</c> lines beneath the scalar and leave invalid YAML.
    /// </summary>
    [Fact]
    public void AScalarTechnologiesValueIsRejectedRatherThanMergedAsASequence()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs
              technologies: csharp
            """;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => HostConfigYaml.WithTechnologies(yaml, ["test"]));

        Assert.Contains("scalar", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Replace the scalar with a YAML sequence", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the exact input string when all requested technologies are already present.
    /// </summary>
    [Fact]
    public void ReturnsUnchangedYamlWhenAllTechnologiesAlreadyPresent()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs
              technologies:
                - csharp
                - test
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["csharp", "test"]);

        Assert.Equal(yaml, result);
    }

    /// <summary>
    /// Respects the indentation level of the parent ontology block (e.g. 4 spaces).
    /// </summary>
    [Fact]
    public void PreservesBlockIndentationMatchingOntologyBlock()
    {
        const string yaml =
            """
            ontology:
                docs-root: docs
            """;

        string result = HostConfigYaml.WithTechnologies(yaml, ["csharp"]);

        Assert.Contains("    technologies:", result, StringComparison.Ordinal);
        Assert.Contains("      - 'csharp'", result, StringComparison.Ordinal);
    }
}
