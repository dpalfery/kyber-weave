using KyberWeave.Core.CopilotPrompt;
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
public sealed class CopilotPromptFileTests : IDisposable
{
    private readonly TempDirectory _home = new();

    public void Dispose() => _home.Dispose();

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
        YamlMappingNode root = ParseRoot(CopilotPromptFile.EmbeddedText());

        string[] keys = root.Children.Keys.Select(KeyText).ToArray();
        string[] expected = ["systemPrompt"];
        Assert.Equal(expected, keys);
    }

    [Fact]
    public void EmbeddedText_IsNonEmptyAndAtMost8000Characters()
    {
        string text = CopilotPromptFile.EmbeddedText();

        Assert.True(text.Length > 0, "the embedded system prompt is empty");
        Assert.True(text.Length <= 8000, $"the embedded system prompt is {text.Length} characters; the limit is 8000");
    }

    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData(".github/copilot-instructions.md")]
    [InlineData(".kyber-weave/kyber-weave.yml")]
    [InlineData("conductor")]
    [InlineData("skill")]
    [InlineData("tool_search_tool")]
    [InlineData("task")]
    public void EmbeddedText_NamesEachInstructionSource(string term)
    {
        string text = CopilotPromptFile.EmbeddedText();

        Assert.True(text.Contains(term, StringComparison.Ordinal), $"the embedded system prompt does not mention '{term}'");
    }

    [Fact]
    public void EmbeddedText_HasSafetyHeadingOrLine()
    {
        string text = CopilotPromptFile.EmbeddedText();

        Assert.True(
            text.Split('\n').Any(line => line.Contains("Safety", StringComparison.Ordinal)),
            "the embedded system prompt has no heading or line containing 'Safety'");
    }

    [Fact]
    public void EmbeddedText_DoesNotContainRetiredDelegationDefault()
    {
        string text = CopilotPromptFile.EmbeddedText();

        Assert.DoesNotContain("Default to doing the work yourself", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_EmbeddedText_ReturnsNull()
    {
        Assert.Null(CopilotPromptFile.Validate(CopilotPromptFile.EmbeddedText()));
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
        string expected = Path.Combine(_home.Path, ".kyber-weave", "copilot", "system-prompt.yaml");

        Assert.Equal(expected, CopilotPromptFile.InstalledPath(_home.Path));
    }
}
