using KyberWeave.Domain.CopilotPrompt;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests.CopilotPrompt;

/// <summary>
/// Pins the shipped Copilot system-prompt text and the validity check applied to a prompt file on disk.
/// </summary>
/// <remarks>
/// The embedded text is what <c>squad copilot-prompt install</c> writes, so its shape is a contract with
/// VS Code: one top-level <c>systemPrompt</c> key, bounded size, and the instruction sources it must cite.
/// </remarks>
public sealed class CopilotPromptFileTests
{
    private static readonly string Home = Path.Combine(new InMemoryFileSystem().Root, "home");

    private static YamlMappingNode ParseRoot(string text)
    {
        YamlStream stream = new();
        stream.Load(new StringReader(text));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string KeyText(YamlNode key) => ((YamlScalarNode)key).Value ?? string.Empty;

    [Fact]
    public void EmbeddedText_ParsesAsYamlWhoseOnlyTopLevelKeyIsSystemPrompt()
    {
        YamlMappingNode root = ParseRoot(CopilotPromptFile.EmbeddedText(ShippedCopilotPrompt.Resources));

        string[] keys = root.Children.Keys.Select(KeyText).ToArray();
        string[] expected = ["systemPrompt"];
        Assert.Equal(expected, keys);
    }

    [Fact]
    public void EmbeddedText_IsNonEmptyAndAtMost8000Characters()
    {
        string text = CopilotPromptFile.EmbeddedText(ShippedCopilotPrompt.Resources);

        Assert.True(text.Length > 0, "the embedded system prompt is empty");
        Assert.True(text.Length <= 8000, $"the embedded system prompt is {text.Length} characters; the limit is 8000");
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData(".github/copilot-instructions.md")]
    [InlineData(".kyber-weave/kyber-weave.yml")]
    [InlineData("conductor")]
    [InlineData("tool_search_tool")]
    [InlineData("codegraph_explore")]
    [InlineData("docs_answer_question_from_repo_documentation")]
    [InlineData("task")]
    public void EmbeddedText_NamesEachInstructionSource(string term)
    {
        string text = CopilotPromptFile.EmbeddedText(ShippedCopilotPrompt.Resources);

        Assert.True(text.Contains(term, StringComparison.Ordinal), $"the embedded system prompt does not mention '{term}'");
    }

    [Fact]
    public void EmbeddedText_HasSafetyHeadingOrLine()
    {
        string text = CopilotPromptFile.EmbeddedText(ShippedCopilotPrompt.Resources);

        Assert.True(
            text.Split('\n').Any(line => line.Contains("Safety", StringComparison.Ordinal)),
            "the embedded system prompt has no heading or line containing 'Safety'");
    }

    [Fact]
    public void Validate_EmbeddedText_ReturnsNull()
    {
        Assert.Null(CopilotPromptFile.Validate(CopilotPromptFile.EmbeddedText(ShippedCopilotPrompt.Resources)));
    }

    [Fact]
    public void Validate_InvalidYaml_ReturnsReason()
    {
        string? reason = CopilotPromptFile.Validate("systemPrompt: [unclosed\n");

        Assert.False(string.IsNullOrWhiteSpace(reason), "invalid YAML must be refused with a reason");
    }

    [Fact]
    public void Validate_UnknownTopLevelKey_ReturnsReason()
    {
        string? reason = CopilotPromptFile.Validate("systemPrompt: |\n  Some text.\nextra: 1\n");

        Assert.False(string.IsNullOrWhiteSpace(reason), "an unknown top-level key must be refused with a reason");
    }

    [Fact]
    public void InstalledPath_IsUnderHomeKyberWeaveCopilotFolder()
    {
        string expected = Path.Combine(Home, ".kyber-weave", "copilot", "system-prompt.yaml");

        Assert.Equal(expected, CopilotPromptFile.InstalledPath(Home));
    }

    [Fact]
    public void SamePath_RelativeSideIsResolvedAgainstTheGivenBaseDirectory()
    {
        FakePathResolver paths = FakePathResolver.OverEmptyFileSystem();
        string home = Path.Combine(Home, "same-path");
        string other = Path.Combine(Home, "other");

        Assert.True(CopilotPromptFile.SamePath(paths, home, "prompts/system-prompt.yaml", Path.Combine(home, "prompts", "system-prompt.yaml")));
        Assert.False(CopilotPromptFile.SamePath(paths, other, "prompts/system-prompt.yaml", Path.Combine(home, "prompts", "system-prompt.yaml")));
    }

    [Fact]
    public void SamePath_IgnoresATrailingSeparatorAndDotSegmentsButNotADifferentFile()
    {
        FakePathResolver paths = FakePathResolver.OverEmptyFileSystem();
        string home = Path.Combine(Home, "dot-segments");

        Assert.True(CopilotPromptFile.SamePath(
            paths,
            home,
            Path.Combine(home, "a", "..", "p.yaml"),
            Path.Combine(home, "p.yaml")));
        Assert.False(CopilotPromptFile.SamePath(
            paths,
            home,
            Path.Combine(home, "p.yaml"),
            Path.Combine(home, "q.yaml")));
    }

    [Fact]
    public void SamePath_ABlankSideIsNotTheSamePathAsAnything()
    {
        FakePathResolver paths = FakePathResolver.OverEmptyFileSystem();

        Assert.False(CopilotPromptFile.SamePath(paths, Home, "", Path.Combine(Home, "p.yaml")));
    }
}
