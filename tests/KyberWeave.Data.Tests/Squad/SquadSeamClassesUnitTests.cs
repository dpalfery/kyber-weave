using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Data.Squad;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves the Squad classes that read one or two files — the model preset validator, the operator channel overlay
/// reader and the Copilot MCP server probe — through the in-memory file system alone.
/// </summary>
public sealed class SquadSeamClassesUnitTests
{
    private const string CopilotRoot = "/home/dev/.copilot";
    private const string SourceRoot = "/work/squad";

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private static SquadModelPreset Preset(string name, string path, params string[] categories) =>
        new(
            name,
            path,
            "digest",
            categories.ToDictionary(
                category => category,
                _ => new SquadModelProfile("inherit", new Dictionary<string, string>()),
                StringComparer.Ordinal));

    private static SquadModelProfiles Bundled(params string[] categories) =>
        new(
            "kyber-squad.model-profiles/v1",
            categories.ToDictionary(
                category => category,
                _ => new SquadModelProfile("inherit", new Dictionary<string, string>()),
                StringComparer.Ordinal),
            "profiles/models.yml");

    private SquadModelPresetValidator Validator => new(_files);

    private SquadOperatorChannelOverlayFile Overlay => new(_files);

    private CopilotMcpServerProbe Probe => new(_files);

    [Fact]
    public void PresetThatNamesOnlyDeclaredCategoriesIsAccepted()
    {
        Validator.Validate(Preset("zai", "/p/zai.yml", "fast"), Bundled("fast", "general"));
    }

    [Fact]
    public void PresetThatNamesAnUndeclaredCategoryIsRefusedAtTheLineThatNamesIt()
    {
        _files.CreateDirectory("/p");
        _files.WriteAllText("/p/zai.yml", "schema: x\nprofiles:\n  fast:\n    default: a\n  bogus:\n    default: b\n");

        SquadSourceValidationException failure = Assert.Throws<SquadSourceValidationException>(() =>
            Validator.Validate(Preset("zai", "/p/zai.yml", "fast", "bogus"), Bundled("fast")));

        Assert.Equal(5, failure.Diagnostics.Items[0].StartLine);
        Assert.Contains("profiles.bogus", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetFileThatIsGoneStillRefusesAtTheFirstLine()
    {
        SquadSourceValidationException failure = Assert.Throws<SquadSourceValidationException>(() =>
            Validator.Validate(Preset("zai", "/p/missing.yml", "bogus"), Bundled("fast")));

        Assert.Equal(1, failure.Diagnostics.Items[0].StartLine);
    }

    [Fact]
    public void OverlayTextIsReadFromTheSourceTree()
    {
        string path = Path.Combine(SourceRoot, "agents", "conductor", "overlays", "operator-channel.md");
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, $"Use {SquadOperatorChannel.ServerPlaceholder}.\r\n");

        SquadOperatorChannelOverlay overlay = Overlay.LoadOverlay(SourceRoot, "ops-relay");

        Assert.Equal("ops-relay/*", overlay.ToolEntry);
        Assert.Equal("Use ops-relay.", overlay.Body);
    }

    [Fact]
    public void MissingOverlayFailsTheRenderInsteadOfShippingAConductorWithoutIt()
    {
        SquadRenderValidationException failure =
            Assert.Throws<SquadRenderValidationException>(() => Overlay.ReadText(SourceRoot));

        Assert.IsAssignableFrom<IOException>(failure.InnerException);
    }

    [Fact]
    public void ServerDeclaredInTheCopilotConfigIsFound()
    {
        WriteConfig("{\"mcpServers\":{\"ops-relay\":{\"command\":\"x\"}}}");

        Assert.True(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Fact]
    public void ConfigWithAUtf8ByteOrderMarkIsStillRead()
    {
        _files.CreateDirectory(CopilotRoot);
        _files.WriteAllBytes(
            Path.Combine(CopilotRoot, "mcp-config.json"),
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("{\"mcpServers\":{\"ops-relay\":{}}}")]);

        Assert.True(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Fact]
    public void ServerNameThatIsOnlyAPrefixOfADeclaredNameIsNotFound()
    {
        WriteConfig("{\"mcpServers\":{\"ops-relay-2\":{}}}");

        Assert.False(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"mcpServers\":[]}")]
    [InlineData("{\"other\":{}}")]
    public void ConfigThatCannotDeclareTheServerAnswersFalse(string config)
    {
        WriteConfig(config);

        Assert.False(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Fact]
    public void MissingConfigAnswersFalse()
    {
        Assert.False(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Fact]
    public void ConfigLargerThanTheBoundIsNeverRead()
    {
        _files.CreateDirectory(CopilotRoot);
        _files.WriteAllBytes(Path.Combine(CopilotRoot, "mcp-config.json"), new byte[CopilotMcpServerProbe.MaxConfigBytes + 1]);

        Assert.False(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Fact]
    public void LinkedConfigIsFollowedToItsRegularFile()
    {
        _files.CreateDirectory("/dotfiles");
        _files.WriteAllText("/dotfiles/mcp.json", "{\"mcpServers\":{\"ops-relay\":{}}}");
        _files.CreateDirectory(CopilotRoot);
        _files.CreateFileLink(Path.Combine(CopilotRoot, "mcp-config.json"), "/dotfiles/mcp.json");

        Assert.True(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Fact]
    public void LinkedConfigWhoseTargetIsGoneAnswersFalse()
    {
        _files.CreateDirectory(CopilotRoot);
        _files.CreateFileLink(Path.Combine(CopilotRoot, "mcp-config.json"), "/dotfiles/gone.json");

        Assert.False(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    [Fact]
    public void LinkedConfigThatNamesADirectoryAnswersFalse()
    {
        _files.CreateDirectory("/dotfiles/dir");
        _files.CreateDirectory(CopilotRoot);
        _files.CreateDirectoryLink(Path.Combine(CopilotRoot, "mcp-config.json"), "/dotfiles/dir");

        Assert.False(Probe.HasServer(CopilotRoot, "ops-relay"));
    }

    private void WriteConfig(string text)
    {
        _files.CreateDirectory(CopilotRoot);
        _files.WriteAllText(Path.Combine(CopilotRoot, "mcp-config.json"), text);
    }
}
