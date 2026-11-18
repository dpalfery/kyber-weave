using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
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

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private FakeSquadUserPaths UserPaths() => new(Path.Combine(_files.Root, "application-data"), Path.Combine(_files.Root, "home"));

    private SquadModelPresetStore Store(FakeSquadUserPaths userPaths) => new(userPaths, _files);

    private static string PresetsDirectory(FakeSquadUserPaths userPaths) =>
        Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");

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
        FakeSquadUserPaths userPaths = UserPaths();
        string presetsDir = PresetsDirectory(userPaths);
        _files.CreateDirectory(presetsDir);

        string presetFilePath = Path.Combine(presetsDir, "dev-models.yml");
        byte[] bytes = Encoding.UTF8.GetBytes(ValidPresetYaml);
        _files.WriteAllBytes(presetFilePath, bytes);
        string expectedDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));

        SquadModelPresetStore store = Store(userPaths);
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
        FakeSquadUserPaths userPaths = UserPaths();
        string presetsDir = PresetsDirectory(userPaths);
        _files.CreateDirectory(presetsDir);

        string presetFilePath = Path.Combine(presetsDir, "bom-preset.yml");
        byte[] preamble = Encoding.UTF8.GetPreamble();
        byte[] contentBytes = Encoding.UTF8.GetBytes(ValidPresetYaml);
        byte[] fileBytesWithBom = new byte[preamble.Length + contentBytes.Length];
        Buffer.BlockCopy(preamble, 0, fileBytesWithBom, 0, preamble.Length);
        Buffer.BlockCopy(contentBytes, 0, fileBytesWithBom, preamble.Length, contentBytes.Length);

        _files.WriteAllBytes(presetFilePath, fileBytesWithBom);
        DateTime lastWriteTimeUtcBefore = _files.GetLastWriteTimeUtc(presetFilePath);

        SquadModelPresetStore store = Store(userPaths);
        SquadModelPreset? preset = store.TryLoad("bom-preset");

        Assert.NotNull(preset);
        Assert.Equal("bom-preset", preset.Name);
        Assert.Equal(presetFilePath, preset.SourcePath);
        Assert.True(preset.Profiles.ContainsKey("general"));

        byte[] fileBytesAfter = _files.ReadAllBytes(presetFilePath);
        DateTime lastWriteTimeUtcAfter = _files.GetLastWriteTimeUtc(presetFilePath);

        Assert.Equal(fileBytesWithBom, fileBytesAfter);
        Assert.Equal(lastWriteTimeUtcBefore, lastWriteTimeUtcAfter);
    }

    [Fact]
    public void TryLoadReturnsNullWhenFileIsMissing()
    {
        FakeSquadUserPaths userPaths = UserPaths();
        SquadModelPresetStore store = Store(userPaths);

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
        FakeSquadUserPaths userPaths = UserPaths();
        SquadModelPresetStore store = Store(userPaths);

        Assert.Throws<ArgumentException>(() => store.TryLoad(invalidName));
    }

    [Fact]
    public void ListReturnsOnlyYmlStemsSortedOrdinally()
    {
        FakeSquadUserPaths userPaths = UserPaths();
        string presetsDir = PresetsDirectory(userPaths);
        _files.CreateDirectory(presetsDir);

        _files.WriteAllText(Path.Combine(presetsDir, "zeta.yml"), ValidPresetYaml);
        _files.WriteAllText(Path.Combine(presetsDir, "alpha.yml"), ValidPresetYaml);
        _files.WriteAllText(Path.Combine(presetsDir, "mid-tier.yml"), ValidPresetYaml);
        _files.WriteAllText(Path.Combine(presetsDir, "not-a-preset.yaml"), ValidPresetYaml);
        _files.WriteAllText(Path.Combine(presetsDir, "notes.txt"), "notes");
        _files.WriteAllText(Path.Combine(presetsDir, "metadata.json"), "{}");

        SquadModelPresetStore store = Store(userPaths);
        IReadOnlyList<string> stems = store.List();

        Assert.Equal(["alpha", "mid-tier", "zeta"], stems);
    }

    [Fact]
    public void ListReturnsEmptyWhenPresetsDirectoryDoesNotExist()
    {
        FakeSquadUserPaths userPaths = UserPaths();
        SquadModelPresetStore store = Store(userPaths);

        IReadOnlyList<string> stems = store.List();

        Assert.Empty(stems);
    }

    [Fact]
    public void EveryConstructorTakesTheFileSystemSeam()
    {
        foreach (System.Reflection.ConstructorInfo constructor in typeof(SquadModelPresetStore).GetConstructors())
        {
            Assert.Contains(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == typeof(KyberWeave.Contracts.Repositories.IFileSystem));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorWithEmptyOrWhitespaceHomeDirectoryThrowsArgumentException(string homeDirectory)
    {
        ISquadUserPaths userPaths = new FakeSquadUserPaths(Path.Combine(_files.Root, "application-data"), homeDirectory);
        Assert.Throws<ArgumentException>(() => new SquadModelPresetStore(userPaths, _files));
    }

    [Fact]
    public void ParseModelPresetRejectsEmptyProfileCategoryNamingCategory()
    {
        const string relativePath = "empty-category.yml";
        const string yaml = """
                            schema: kyber-squad.model-preset/v1
                            profiles:
                              fast: {}
                            """;
        byte[] bytes = Encoding.UTF8.GetBytes(yaml);

        Diagnostic diagnostic = AssertSourceValidationException(
            () => SquadSourceLoader.ParseModelPreset(relativePath, bytes),
            relativePath,
            "fast",
            3);

        Assert.Equal(3, diagnostic.StartLine);
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
