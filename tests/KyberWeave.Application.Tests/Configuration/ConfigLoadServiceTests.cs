using KyberWeave.Application.Configuration;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Application.Tests.Configuration;

/// <summary>
/// A docs-root override replaces the ontology's roots and nothing else. Failure this guards: the override
/// rebuilt the config from four of its sections and silently reset the rest to their defaults.
/// </summary>
public sealed class ConfigLoadServiceTests
{
    [Fact]
    public void ADocsRootOverrideKeepsEveryOtherSetting()
    {
        KyberWeaveConfig configured = new KyberWeaveConfig
        {
            StopsOnToolFailure = false,
            WorktreeIndexHook = false,
        }.WithConfigReg(ConfigRegConfig.ProductDefaults);
        ConfigLoadService service = new(new FixedConfigs(configured));

        bool loaded = service.TryLoadWithDocsRoots(
            "/repo",
            configPath: null,
            ["docs"],
            new DiagnosticReport(),
            out KyberWeaveConfig config,
            out OntologyConfig ontology);

        Assert.True(loaded);
        Assert.Equal(["docs"], ontology.DocsRoots);
        Assert.False(config.StopsOnToolFailure);
        Assert.False(config.WorktreeIndexHook);
        Assert.Same(configured.Review, config.Review);
        Assert.Same(configured.Arbiter, config.Arbiter);
        Assert.Same(configured.Mcp, config.Mcp);
        Assert.Same(configured.ConfigReg, config.ConfigReg);
    }

    private sealed class FixedConfigs(KyberWeaveConfig config) : IKyberWeaveConfigRepository
    {
        public KyberWeaveConfig Load(string repoRoot, string? configPath = null) => config;

        public KyberWeaveConfigLoadResult TryLoad(string repoRoot, string? configPath = null) =>
            KyberWeaveConfigLoadResultFactory.Ok(config, configPath);

        public string? FindConfigPath(string repoRoot) => null;

        public KyberWeaveConfig LoadUserConfig(string homeDirectory, string? configPath = null) => config;

        public KyberWeaveConfigLoadResult TryLoadUserConfig(string homeDirectory, string? configPath = null) =>
            KyberWeaveConfigLoadResultFactory.Ok(config, configPath);
    }
}
