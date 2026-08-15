using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using YamlDotNet.Core;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// K3 RED contract for the Squad section of <c>kyber-weave.yml</c>.
/// Configuration must merge without changing existing host-config semantics, while an
/// explicitly supplied list replaces its product or host default in full.
/// </summary>
public sealed class SquadConfigurationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void ProductDefaults_UseFullBundleAndBestEffortWithoutPinnedTargets()
    {
        var config = SquadConfig.ProductDefaults;

        Assert.Equal("full", config.Bundle);
        Assert.Null(config.Version);
        Assert.Empty(config.Targets);
        Assert.Empty(config.Exclusions);
        Assert.Equal(SquadTranslationMode.BestEffort, config.Translation);
    }

    [Fact]
    public void CombinedConfigLoader_BindsCompleteSquadSection()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml("""
            squad:
              bundle: full
              version: 1.2.3
              targets: [codex, github-copilot, factory-droids]
              exclusions: [warp]
              translation: best-effort
            """);

        Assert.Equal("full", config.Squad.Bundle);
        Assert.Equal("1.2.3", config.Squad.Version);
        Assert.Equal(
            [SquadTarget.Codex, SquadTarget.Copilot, SquadTarget.Factory],
            config.Squad.Targets);
        Assert.Equal([SquadTarget.Warp], config.Squad.Exclusions);
        Assert.Equal(SquadTranslationMode.BestEffort, config.Squad.Translation);
    }

    [Fact]
    public void LoadMerged_OmittedValuesPreserveDefaults()
    {
        var defaults = new SquadConfig
        {
            Bundle = "full",
            Version = "1.2.3",
            Targets = [SquadTarget.Codex, SquadTarget.Cursor],
            Exclusions = [SquadTarget.Warp],
            Translation = SquadTranslationMode.BestEffort
        };
        var path = WriteConfig("""
            squad:
              translation: best-effort
            """);

        var config = SquadConfigLoader.LoadMerged(defaults, path);

        Assert.Equal(defaults.Bundle, config.Bundle);
        Assert.Equal(defaults.Version, config.Version);
        Assert.Equal(defaults.Targets, config.Targets);
        Assert.Equal(defaults.Exclusions, config.Exclusions);
        Assert.Equal(defaults.Translation, config.Translation);
    }

    [Fact]
    public void LoadMerged_PresentListsReplaceDefaultsRatherThanAppend()
    {
        var defaults = new SquadConfig
        {
            Targets = [SquadTarget.Codex, SquadTarget.Cursor],
            Exclusions = [SquadTarget.Warp]
        };
        var path = WriteConfig("""
            squad:
              targets: [claude]
              exclusions: [kilo, gemini]
            """);

        var config = SquadConfigLoader.LoadMerged(defaults, path);

        Assert.Equal([SquadTarget.Claude], config.Targets);
        Assert.Equal([SquadTarget.Kilo, SquadTarget.Gemini], config.Exclusions);
    }

    [Fact]
    public void LoadMerged_ExplicitEmptyListsClearDefaults()
    {
        var defaults = new SquadConfig
        {
            Targets = [SquadTarget.Codex],
            Exclusions = [SquadTarget.Warp]
        };
        var path = WriteConfig("""
            squad:
              targets: []
              exclusions: []
            """);

        var config = SquadConfigLoader.LoadMerged(defaults, path);

        Assert.Empty(config.Targets);
        Assert.Empty(config.Exclusions);
    }

    [Theory]
    [InlineData(null, "1.2.3+build.42", "1.2.3")]
    [InlineData("1.2.3", "1.2.3+build.42", "1.2.3")]
    public void ResolveVersion_UsesNormalizedCliVersionAndRequiresAnExactPin(
        string? configuredVersion,
        string cliVersion,
        string expected)
    {
        var config = new SquadConfig { Version = configuredVersion };

        var effective = SquadConfigLoader.ResolveVersion(config, cliVersion);

        Assert.Equal(expected, effective);
    }

    [Fact]
    public void ResolveVersion_MismatchedConfiguredVersionIsRejected()
    {
        var config = new SquadConfig { Version = "1.2.4" };

        var exception = Assert.Throws<YamlException>(
            () => SquadConfigLoader.ResolveVersion(config, "1.2.3+build.42"));

        Assert.Contains("squad.version", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1.2.3", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.2.3+.")]
    [InlineData("1.2.3+..")]
    [InlineData("1.2.3+.build")]
    [InlineData("1.2.3+build.")]
    [InlineData("1.2.3+build..42")]
    public void ResolveVersion_BuildMetadataWithEmptyIdentifierIsRejected(string cliVersion)
    {
        var config = new SquadConfig();

        var exception = Assert.Throws<YamlException>(
            () => SquadConfigLoader.ResolveVersion(config, cliVersion));

        Assert.Contains("stable X.Y.Z", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bundle", "not-full")]
    [InlineData("translation", "strict")]
    [InlineData("target", "not-a-target")]
    [InlineData("version", "v1.2.3")]
    public void CombinedTryLoad_InvalidSquadValueReportsKwConfig001(
        string invalidField,
        string invalidValue)
    {
        var body = invalidField switch
        {
            "bundle" => $"bundle: {invalidValue}",
            "translation" => $"translation: {invalidValue}",
            "target" => $"targets: [{invalidValue}]",
            "version" => $"version: {invalidValue}",
            _ => throw new InvalidOperationException($"Unexpected fixture field '{invalidField}'.")
        };
        WriteRepositoryConfig($"squad:\n  {body}\n");

        var result = KyberWeaveConfigLoader.TryLoad(_temp.Path);

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.NotNull(result.Error);
        Assert.Contains($"squad.{(invalidField == "target" ? "targets" : invalidField)}", result.Error, StringComparison.Ordinal);
        Assert.Equal("KW-CONFIG-001", KyberWeaveConfigLoader.ConfigLoadErrorCode);
    }

    public void Dispose() => _temp.Dispose();

    private string WriteConfig(string yaml)
    {
        var path = Path.Combine(_temp.Path, "squad-config.yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private void WriteRepositoryConfig(string yaml)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_temp.Path, ".kyber-weave"));
        File.WriteAllText(Path.Combine(directory.FullName, "kyber-weave.yml"), yaml);
    }
}
