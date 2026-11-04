using KyberWeave.Core.Configuration;
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
    /// A byte-order mark is a character, not a line break, so one written ahead of the first line
    /// must not hide the <c>ontology:</c> key that starts immediately after it. Read as "start of
    /// line" alone, the key goes unfound and a second <c>ontology</c> block is appended — two
    /// keys, which YAML refuses to load.
    /// </summary>
    [Fact]
    public void AByteOrderMarkBeforeTheFirstLineDoesNotHideTheOntologyKey()
    {
        const string yaml = "\uFEFFontology:\n  docs-root: docs\n";

        Assert.Equal(yaml, HostConfigYaml.WithDocsRoot(yaml, "docs"));
    }

    /// <summary>
    /// The technologies edit reads the same line endings the configuration was written with. A
    /// lone-CR file is one long line to a <c>'\n'</c> split, so the <c>ontology:</c> key goes
    /// unfound and a second block is appended — two <c>ontology</c> keys, which YAML refuses to
    /// load at all.
    /// </summary>
    [Fact]
    public void AddsTechnologiesToALoneCrConfigurationWithoutASecondOntologyBlock()
    {
        const string yaml = "ontology:\r  docs-root: docs\r";

        string result = HostConfigYaml.WithTechnologies(yaml, ["csharp"]);

        Assert.Contains("csharp", KyberWeaveConfigLoader.LoadFromYaml(result).Ontology.Technologies);
        Assert.DoesNotContain("\n", result, StringComparison.Ordinal);
        Assert.Contains("  technologies:\r", result, StringComparison.Ordinal);
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

    /// <summary>
    /// An existing <c>arbiter.enabled: false</c> is flipped in place. Only the value changes,
    /// so the inline comment on that line and every other line survive.
    /// </summary>
    [Fact]
    public void FlipsAnExistingArbiterEnabledFalseToTrue()
    {
        const string yaml =
            """
            ontology:
              docs-root: docs

            arbiter:
              enabled: false  # enabled by hand for now
            """;

        string result = HostConfigYaml.WithArbiterEnabled(yaml);

        Assert.Equal(
            yaml.Replace("enabled: false", "enabled: true", StringComparison.Ordinal),
            result);
    }

    /// <summary>
    /// A configuration without an <c>arbiter:</c> block gains one at the end, and every byte
    /// that was already in the file stays where it was.
    /// </summary>
    [Fact]
    public void AppendsAnArbiterBlockWithoutChangingTheRestOfTheFile()
    {
        const string yaml =
            """
            # Hand-maintained host configuration
            ontology:
              docs-root: docs

            harness:
              profiles:
                claude:
                  directory-name: .agents/agents

            """;

        string result = HostConfigYaml.WithArbiterEnabled(yaml);

        Assert.StartsWith(yaml, result, StringComparison.Ordinal);
        Assert.Contains("\narbiter:\n  enabled: true", result, StringComparison.Ordinal);
        YamlScalarNode enabled =
            (YamlScalarNode)ReadArbiter(result).Children[new YamlScalarNode("enabled")];
        Assert.Equal("true", enabled.Value);
    }

    /// <summary>
    /// An already-enabled configuration is returned unchanged, so a second install run is a
    /// no-op rather than a rewrite of the operator's file.
    /// </summary>
    [Fact]
    public void ReturnsAnAlreadyEnabledConfigurationUnchanged()
    {
        const string yaml =
            """
            arbiter:
              enabled: true
            """;

        string result = HostConfigYaml.WithArbiterEnabled(yaml);

        Assert.Equal(yaml, result);
        Assert.Equal(yaml, HostConfigYaml.WithArbiterEnabled(result));
    }

    /// <summary>
    /// Only the enabled value changes: full-line comments, inline comments, and the order of
    /// the surrounding keys are preserved byte-for-byte.
    /// </summary>
    [Fact]
    public void PreservesCommentsAndKeyOrderWhenFlippingEnabled()
    {
        const string yaml =
            """
            # Host configuration, hand-maintained.
            ontology:
              docs-root: docs

            arbiter:
              # Switched off until the roll-out lands.
              provider:
                kind: none
              enabled: false  # flip this, keep the comment
              rules:
                - id: KW-ARB-SCOPE-002
            """;

        string result = HostConfigYaml.WithArbiterEnabled(yaml);

        Assert.Equal(
            yaml.Replace("enabled: false", "enabled: true", StringComparison.Ordinal),
            result);
        Assert.Contains(
            "# Switched off until the roll-out lands.",
            result,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A CRLF configuration keeps its line endings: the appended block uses CRLF and no bare
    /// LF is introduced anywhere in the file.
    /// </summary>
    [Fact]
    public void AddsTheArbiterBlockWithCrlfLineEndings()
    {
        const string yaml =
            "# Hand-maintained host configuration\r\nontology:\r\n  docs-root: docs\r\n";

        string result = HostConfigYaml.WithArbiterEnabled(yaml);

        Assert.StartsWith(yaml, result, StringComparison.Ordinal);
        Assert.Contains("\r\narbiter:\r\n  enabled: true", result, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\n",
            result.Replace("\r\n", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
        YamlScalarNode enabled =
            (YamlScalarNode)ReadArbiter(result).Children[new YamlScalarNode("enabled")];
        Assert.Equal("true", enabled.Value);
    }

    /// <summary>
    /// A byte-order mark is a character, not a line break. When it precedes the first
    /// <c>arbiter:</c> key, the merge must find that key rather than append a second block.
    /// </summary>
    [Fact]
    public void AByteOrderMarkBeforeTheArbiterKeyDoesNotCauseASecondBlock()
    {
        const string yaml = "\uFEFFarbiter:\n  enabled: false\n";

        string result = HostConfigYaml.WithArbiterEnabled(yaml);

        Assert.Equal("\uFEFFarbiter:\n  enabled: true\n", result);
    }

    /// <summary>
    /// An existing provider and rules block is operator state and survives the enabled flip
    /// with every value, key, and comment intact.
    /// </summary>
    [Fact]
    public void PreservesAnExistingArbiterProviderAndRulesBlock()
    {
        const string yaml =
            """
            arbiter:
              enabled: false
              provider:
                kind: systemone
                endpoint: https://api.example.test/v1
                model: pinned-model
                timeout-ms: 3000
              rules:
                - id: KW-ARB-SCOPE-002
                  confidence-at-least: 0.85
                - id: HOST-MIGRATIONS-001
                  trigger: delegate
                  question: Does the task touch database migrations?
                  answers: [touches, clear]
                  effects: { touches: escalate, clear: allow }
            """;

        string result = HostConfigYaml.WithArbiterEnabled(yaml);

        Assert.Equal(
            yaml.Replace("enabled: false", "enabled: true", StringComparison.Ordinal),
            result);
    }

    private static YamlMappingNode ReadArbiter(string yaml)
    {
        YamlStream stream = new YamlStream();
        using StringReader reader = new StringReader(yaml);
        stream.Load(reader);
        YamlMappingNode root = (YamlMappingNode)stream.Documents[0].RootNode;
        return (YamlMappingNode)root.Children[new YamlScalarNode("arbiter")];
    }
}
