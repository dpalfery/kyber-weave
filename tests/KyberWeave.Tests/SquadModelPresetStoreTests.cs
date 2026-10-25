using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for Squad model preset parsing, schema validation, and file-system store resolution.
/// </summary>
public sealed class SquadModelPresetStoreTests
{
    private const string ValidPresetYaml = """
                                           schema: kyber-squad.model-preset/v1
                                           profiles:
                                             deep-planning:
                                               default: inherit
                                               claude: claude-3-7-sonnet
                                               copilot: inherit
                                             general:
                                               default: gpt-4o
                                               cursor: gpt-4o-mini
                                           """;

    [Fact]
    public void ValidPresetFileLoadsWithComputedDigestAndProfiles()
    {
        const string relativePath = "valid-preset.yml";
        byte[] bytes = Encoding.UTF8.GetBytes(ValidPresetYaml);
        string expectedDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset(relativePath, bytes);

        Assert.Equal("valid-preset", preset.Name);
        Assert.Equal(relativePath, preset.SourcePath);
        Assert.Equal(expectedDigest, preset.Digest);
        Assert.Equal(2, preset.Profiles.Count);

        Assert.True(preset.Profiles.TryGetValue("deep-planning", out SquadModelProfile? deepPlanning));
        Assert.NotNull(deepPlanning);
        Assert.Equal("inherit", deepPlanning.Default);
        Assert.Equal("claude-3-7-sonnet", deepPlanning.HarnessModels["claude"]);
        Assert.Equal("inherit", deepPlanning.HarnessModels["copilot"]);

        Assert.True(preset.Profiles.TryGetValue("general", out SquadModelProfile? general));
        Assert.NotNull(general);
        Assert.Equal("gpt-4o", general.Default);
        Assert.Equal("gpt-4o-mini", general.HarnessModels["cursor"]);
    }

    [Fact]
    public void PresetCategoryMayOmitDefaultAndKeepsInheritValue()
    {
        const string relativePath = "inherit-only.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v1
                            profiles:
                              fast:
                                copilot: inherit
                                claude: claude-haiku-5-5
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        SquadModelPreset preset = SquadSourceLoader.ParseModelPreset(relativePath, bytes);

        Assert.True(preset.Profiles.TryGetValue("fast", out SquadModelProfile? fast));
        Assert.NotNull(fast);
        Assert.Null(fast.Default);
        Assert.Equal("inherit", fast.HarnessModels["copilot"]);
        Assert.Equal("claude-haiku-5-5", fast.HarnessModels["claude"]);
    }

    [Fact]
    public void ParseModelPresetRejectsInvalidSchemaNamingFileAndLine()
    {
        const string relativePath = "invalid-schema.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v2
                            profiles:
                              general:
                                default: inherit
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        Diagnostic diagnostic = AssertSourceValidationException(
            () => SquadSourceLoader.ParseModelPreset(relativePath, bytes),
            relativePath,
            "kyber-squad.model-preset/v1",
            1);

        Assert.Equal(1, diagnostic.StartLine);
    }

    [Fact]
    public void ParseModelPresetRejectsUnknownTopLevelKeyNamingFileAndLine()
    {
        const string relativePath = "unexpected-top-level.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v1
                            unexpected_field: disallowed
                            profiles:
                              general:
                                default: inherit
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        Diagnostic diagnostic = AssertSourceValidationException(
            () => SquadSourceLoader.ParseModelPreset(relativePath, bytes),
            relativePath,
            "unexpected_field",
            2);

        Assert.Equal(2, diagnostic.StartLine);
    }

    [Fact]
    public void ParseModelPresetRejectsUnknownHarnessNamingFileAndLine()
    {
        const string relativePath = "unknown-harness.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v1
                            profiles:
                              general:
                                default: inherit
                                unsupported_harness: gpt-5
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        Diagnostic diagnostic = AssertSourceValidationException(
            () => SquadSourceLoader.ParseModelPreset(relativePath, bytes),
            relativePath,
            "unsupported_harness",
            5);

        Assert.Equal(5, diagnostic.StartLine);
    }

    [Fact]
    public void ParseModelPresetRejectsDuplicateCategoryNamingFileAndLine()
    {
        const string relativePath = "duplicate-category.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v1
                            profiles:
                              general:
                                default: inherit
                              general:
                                default: gpt-4o
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        Diagnostic diagnostic = AssertSourceValidationException(
            () => SquadSourceLoader.ParseModelPreset(relativePath, bytes),
            relativePath,
            "general",
            5);

        Assert.Equal(5, diagnostic.StartLine);
    }

    [Fact]
    public void ParseModelPresetRejectsDuplicateHarnessNamingFileAndLine()
    {
        const string relativePath = "duplicate-harness.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v1
                            profiles:
                              general:
                                claude: claude-3-5-sonnet
                                claude: claude-3-7-sonnet
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        Diagnostic diagnostic = AssertSourceValidationException(
            () => SquadSourceLoader.ParseModelPreset(relativePath, bytes),
            relativePath,
            "claude",
            5);

        Assert.Equal(5, diagnostic.StartLine);
    }

    [Fact]
    public void ParseModelPresetRejectsMalformedYamlNamingFileAndLine()
    {
        const string relativePath = "malformed.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v1
                            profiles: [unclosed
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        Diagnostic diagnostic = AssertSourceValidationException(
            () => SquadSourceLoader.ParseModelPreset(relativePath, bytes),
            relativePath,
            "YAML",
            null);

        Assert.NotNull(diagnostic.StartLine);
    }

    [Fact]
    public void TryLoadReturnsPresetWithMatchingMetadataWhenFileExists()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);

        string presetFilePath = Path.Combine(presetsDir, "dev-models.yml");
        byte[] bytes = Encoding.UTF8.GetBytes(ValidPresetYaml);
        File.WriteAllBytes(presetFilePath, bytes);
        string expectedDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        SquadModelPresetStore store = new(userPaths);
        SquadModelPreset? preset = store.TryLoad("dev-models");

        Assert.NotNull(preset);
        Assert.Equal("dev-models", preset.Name);
        Assert.Equal(presetFilePath, preset.SourcePath);
        Assert.Equal(expectedDigest, preset.Digest);
        Assert.True(preset.Profiles.ContainsKey("general"));
    }

    [Fact]
    public void TryLoadLoadsPresetWithUtf8BomPreambleWithoutThrowingOrModifyingFileOnDisk()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);

        string presetFilePath = Path.Combine(presetsDir, "bom-preset.yml");
        byte[] preamble = Encoding.UTF8.GetPreamble();
        byte[] contentBytes = Encoding.UTF8.GetBytes(ValidPresetYaml);
        byte[] fileBytesWithBom = new byte[preamble.Length + contentBytes.Length];
        Buffer.BlockCopy(preamble, 0, fileBytesWithBom, 0, preamble.Length);
        Buffer.BlockCopy(contentBytes, 0, fileBytesWithBom, preamble.Length, contentBytes.Length);

        File.WriteAllBytes(presetFilePath, fileBytesWithBom);
        DateTime lastWriteTimeUtcBefore = File.GetLastWriteTimeUtc(presetFilePath);

        SquadModelPresetStore store = new(userPaths);
        SquadModelPreset? preset = store.TryLoad("bom-preset");

        Assert.NotNull(preset);
        Assert.Equal("bom-preset", preset.Name);
        Assert.Equal(presetFilePath, preset.SourcePath);
        Assert.True(preset.Profiles.ContainsKey("general"));

        byte[] fileBytesAfter = File.ReadAllBytes(presetFilePath);
        DateTime lastWriteTimeUtcAfter = File.GetLastWriteTimeUtc(presetFilePath);

        Assert.Equal(fileBytesWithBom, fileBytesAfter);
        Assert.Equal(lastWriteTimeUtcBefore, lastWriteTimeUtcAfter);
    }

    [Fact]
    public void TryLoadReturnsNullWhenFileIsMissing()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadModelPresetStore store = new(userPaths);

        SquadModelPreset? preset = store.TryLoad("nonexistent-preset");

        Assert.Null(preset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-hyphen")]
    [InlineData("Uppercase")]
    [InlineData("has_underscore")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    [InlineData("../traversal")]
    [InlineData("a123456789b123456789c123456789d123456789e123456789f123456789g12345")]
    public void TryLoadThrowsArgumentExceptionWhenNameIsInvalid(string invalidName)
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadModelPresetStore store = new(userPaths);

        Assert.Throws<ArgumentException>(() => store.TryLoad(invalidName));
    }

    [Fact]
    public void ListReturnsOnlyYmlStemsSortedOrdinally()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
        Directory.CreateDirectory(presetsDir);

        File.WriteAllText(Path.Combine(presetsDir, "zeta.yml"), ValidPresetYaml);
        File.WriteAllText(Path.Combine(presetsDir, "alpha.yml"), ValidPresetYaml);
        File.WriteAllText(Path.Combine(presetsDir, "mid-tier.yml"), ValidPresetYaml);
        File.WriteAllText(Path.Combine(presetsDir, "not-a-preset.yaml"), ValidPresetYaml);
        File.WriteAllText(Path.Combine(presetsDir, "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(presetsDir, "metadata.json"), "{}");

        SquadModelPresetStore store = new(userPaths);
        IReadOnlyList<string> stems = store.List();

        Assert.Equal(["alpha", "mid-tier", "zeta"], stems);
    }

    [Fact]
    public void ListReturnsEmptyWhenPresetsDirectoryDoesNotExist()
    {
        using TempDirectory temp = new();
        FakeSquadUserPaths userPaths = new(temp.Path);
        SquadModelPresetStore store = new(userPaths);

        IReadOnlyList<string> stems = store.List();

        Assert.Empty(stems);
    }

    private static Diagnostic AssertSourceValidationException(
        Action action,
        string expectedPath,
        string expectedMessageOrHintFragment,
        int? expectedLine)
    {
        SquadSourceValidationException exception = Assert.Throws<SquadSourceValidationException>(action);
        Diagnostic diagnostic = Assert.Single(
            exception.Diagnostics.Items,
            item => string.Equals(item.FilePath, expectedPath, StringComparison.Ordinal) &&
                    (item.Message.Contains(expectedMessageOrHintFragment, StringComparison.OrdinalIgnoreCase) ||
                     (item.Hint?.Contains(expectedMessageOrHintFragment, StringComparison.OrdinalIgnoreCase) ??
                      false)));

        Assert.Equal(Severity.Error, diagnostic.Severity);
        if (expectedLine.HasValue)
            Assert.Equal(expectedLine.Value, diagnostic.StartLine);
        else
            Assert.NotNull(diagnostic.StartLine);

        return diagnostic;
    }
}
