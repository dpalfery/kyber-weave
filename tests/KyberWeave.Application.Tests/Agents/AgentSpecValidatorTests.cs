using KyberWeave.Application.Agents;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Routing;
using KyberWeave.Core.Agents.Security;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Agents;

public class AgentSpecValidatorTests
{
    private readonly InMemoryFileSystem files = new();

    private readonly string Root;

    public AgentSpecValidatorTests()
    {
        Root = Path.Combine(files.Root, "agents");
        files.CreateDirectory(Root);
    }

    private AgentSpecValidator Validator => ValidatorOver(files);

    private static AgentSpecValidator ValidatorOver(InMemoryFileSystem fileSystem) =>
        new(fileSystem, new FakePathResolver(fileSystem));

    [Fact]
    public void ValidateWhenNameMissingEmitsKwAgentSpec001()
    {
        AgentModel agent = new AgentModel
        {
            RoleName = "",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "test.md"),
            DirectoryPath = Root,
            Description = "Use when querying databases.",
            InstructionsBody = "Instructions"
        };

        DiagnosticReport report = Validator.Validate(agent);

        Assert.Contains(report.Items, d => d is { Code: AgentSpecValidator.RuleMissingName, Severity: Severity.Error });
    }

    [Fact]
    public void ValidateWhenDescriptionMissingEmitsKwAgentSpec002()
    {
        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "test.md"),
            DirectoryPath = Root,
            Description = "",
            InstructionsBody = "Instructions"
        };

        DiagnosticReport report = Validator.Validate(agent);

        Assert.Contains(report.Items, d => d is { Code: AgentSpecValidator.RuleMissingDescription, Severity: Severity.Warning });
    }

    [Fact]
    public void ValidateWhenInstructionsMissingEmitsKwAgentSpec003()
    {
        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "test.md"),
            DirectoryPath = Root,
            Description = "Use when querying databases.",
            InstructionsBody = ""
        };

        DiagnosticReport report = Validator.Validate(agent);

        Assert.Contains(report.Items, d => d is { Code: AgentSpecValidator.RuleMissingInstructions, Severity: Severity.Error });
    }

    [Fact]
    public void ValidateWhenMarkdownLinkReferencesNonexistentFileEmitsKwAgentSpec004()
    {
        string agentDirPath = Root;

        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(agentDirPath, "agent.md"),
            DirectoryPath = agentDirPath,
            Description = "Use this agent to do work. See [guide](docs/missing.md) for details.",
            InstructionsBody = "You are a helpful agent."
        };

        DiagnosticReport report = Validator.Validate(agent);

        Assert.Contains(report.Items, d => d is { Code: "KW-AGENT-SPEC-004", Severity: Severity.Error });
    }

    [Fact]
    public void ValidateWhenInlineLinkReferencesNonexistentFileEmitsKwAgentSpec004()
    {
        string agentDirPath = Root;

        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(agentDirPath, "agent.md"),
            DirectoryPath = agentDirPath,
            Description = "Use when setting up configurations.",
            InstructionsBody = "Instructions: Check the configuration in `references/missing.md` before proceeding."
        };

        DiagnosticReport report = Validator.Validate(agent);

        Assert.Contains(report.Items, d => d is { Code: "KW-AGENT-SPEC-004", Severity: Severity.Error });
    }

    [Fact]
    public void ValidateWhenFileReferenceResolvesSuccessfullyEmitsNoKwAgentSpec004()
    {
        string agentDirPath = Root;
        string referencesDir = Path.Combine(agentDirPath, "references");
        files.CreateDirectory(referencesDir);
        string existingFile = Path.Combine(referencesDir, "existing.md");
        files.WriteAllText(existingFile, "# Reference\n\nThis is an existing file.");

        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(agentDirPath, "agent.md"),
            DirectoryPath = agentDirPath,
            Description = "Use when working with references.",
            InstructionsBody = "Instructions: See `references/existing.md` for details."
        };

        DiagnosticReport report = Validator.Validate(agent);

        Assert.DoesNotContain(report.Items, d => d.Code == "KW-AGENT-SPEC-004");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DifferentlyCasedReferencesAreCheckedAccordingToTheFilesystem(bool caseSensitive)
    {
        InMemoryFileSystem fileSystem = new(caseSensitive: caseSensitive);
        string referencesDir = Path.Combine(Root, "references");
        fileSystem.CreateDirectory(referencesDir);
        fileSystem.WriteAllText(Path.Combine(referencesDir, "Guide.md"), "# Guide");

        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "agent.md"),
            DirectoryPath = Root,
            Description = "Use when working with references.",
            InstructionsBody = "See `references/Guide.md`, `references/guide.md`, " +
                "`references/Missing.md`, and `references/missing.md`."
        };

        Diagnostic[] broken = ValidatorOver(fileSystem).Validate(agent).Items
            .Where(d => d.Code == AgentSpecValidator.RuleBrokenFileReference).ToArray();

        if (!caseSensitive)
        {
            Diagnostic diagnostic = Assert.Single(broken);
            Assert.Contains("references/Missing.md", diagnostic.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(3, broken.Length);
            Assert.Contains(broken, d => d.Message.Contains("references/guide.md", StringComparison.Ordinal));
            Assert.Contains(broken, d => d.Message.Contains("references/Missing.md", StringComparison.Ordinal));
            Assert.Contains(broken, d => d.Message.Contains("references/missing.md", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void OnlyCompleteInlineCodePathsAreChecked()
    {
        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "agent.md"),
            DirectoryPath = Root,
            Description = "Use when working with references.",
            InstructionsBody = "Plain references/plain.md, https://example.com/references/url.md, " +
        "`prefix references/partial.md`, and `references/missing.md`.\n\n" +
        "```text\nreferences/fenced.md\n```"
        };

        Diagnostic diagnostic = Assert.Single(Validator.Validate(agent).Items,
            d => d.Code == AgentSpecValidator.RuleBrokenFileReference);
        Assert.Contains("references/missing.md", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UppercaseSchemesAndForeignAbsolutePathsAreIgnored()
    {
        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "agent.md"),
            DirectoryPath = Root,
            Description = "Use when working with references.",
            InstructionsBody = "[http](HTTP://example.com/missing.md) " +
        "[https](HTTPS://example.com/missing.md) [email](MAILTO:user@example.com) " +
        "[drive](C:/missing.md) [unc](<\\\\server\\share\\missing.md>)"
        };

        Assert.DoesNotContain(Validator.Validate(agent).Items,
            d => d.Code == AgentSpecValidator.RuleBrokenFileReference);
    }

    [Fact]
    public void NestedMissingReferenceSuggestsPathRelativeToAgentDirectory()
    {
        string referencesDir = Path.Combine(Root, "references");
        files.CreateDirectory(referencesDir);
        files.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide");

        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "agent.md"),
            DirectoryPath = Root,
            Description = "Use when working with references.",
            InstructionsBody = "See `references/guid.md`."
        };

        Diagnostic diagnostic = Assert.Single(Validator.Validate(agent).Items,
            d => d.Code == AgentSpecValidator.RuleBrokenFileReference);
        Assert.Contains("references/guide.md", diagnostic.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingReferenceDirectoryStillReportsFallbackHint()
    {
        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(Root, "agent.md"),
            DirectoryPath = Root,
            Description = "Use when working with references.",
            InstructionsBody = "See `references/missing.md`."
        };

        Diagnostic diagnostic = Assert.Single(Validator.Validate(agent).Items,
            d => d.Code == AgentSpecValidator.RuleBrokenFileReference);
        Assert.Contains("Relative file path does not exist", diagnostic.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyReferenceDirectoryNeverSuggestsPathOutsideAgentDirectory()
    {
        string agentDir = Path.Combine(Root, "agent");
        files.CreateDirectory(Path.Combine(agentDir, "references"));
        files.WriteAllText(Path.Combine(Root, "guide.md"), "# Guide");

        AgentModel agent = new AgentModel
        {
            RoleName = "test-agent",
            Harness = HarnessKind.Claude,
            FilePath = Path.Combine(agentDir, "agent.md"),
            DirectoryPath = agentDir,
            Description = "Use when working with references.",
            InstructionsBody = "See `references/guide.md`."
        };

        Diagnostic diagnostic = Assert.Single(Validator.Validate(agent).Items,
            d => d.Code == AgentSpecValidator.RuleBrokenFileReference);
        Assert.DoesNotContain("..", diagnostic.Hint, StringComparison.Ordinal);
        Assert.Contains("Relative file path does not exist", diagnostic.Hint, StringComparison.Ordinal);
    }
}
