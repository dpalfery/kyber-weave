using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Data.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// How the Arbiter reads the user's host configuration: the rules it takes from the defaults and the user's file, the
/// global targets a missing, empty, malformed or unreadable file yields, the YAML edits that keep targets unique, and the
/// way an explicit config path or an empty repository root fails. Regression tests for Kilo review comments 7 through 10
/// and 12 on PR #527, run over an in-memory file system.
/// </summary>
public sealed class ArbiterUserConfigRemediationTests
{
    private readonly InMemoryFileSystem _files = new();
    private readonly string _home;

    public ArbiterUserConfigRemediationTests()
    {
        _home = Path.Combine(_files.Root, "home");
        _files.CreateDirectory(_home);
    }

    [Fact]
    public void Item7_ArbiterConfigLoader_LoadUserConfig_PopulatesRulesFromDefaultsAndUserYaml()
    {
        string yaml = """
            arbiter:
              enabled: true
              rules:
                - id: custom-rule
                  trigger: delegate
                  question: Does this task touch custom user rules?
                  answers: [deny, allow]
                  effects: { deny: deny, allow: allow }
            """;

        ArbiterConfig config = ArbiterConfigLoader.LoadUserConfig(yaml);
        Assert.NotEmpty(config.Rules);
        Assert.Contains(config.Rules, r => r.Id == "custom-rule");
        Assert.Contains(config.Rules, r => r.Id.StartsWith("KW-ARB-", StringComparison.Ordinal));
    }

    [Fact]
    public void Item8_UserHostConfig_ReadGlobalTargets_NullEmptyOrMalformedYaml_ReturnsEmptyList()
    {
        string configPath = UserHostConfigPath.Of(_home);
        _files.CreateDirectory(Path.GetDirectoryName(configPath)!);
        FileUserHostConfigStore store = new(_files);

        _files.WriteAllText(configPath, "");
        Assert.Empty(store.ReadGlobalTargets(_home));

        _files.WriteAllText(configPath, "   \n\r\n  ");
        Assert.Empty(store.ReadGlobalTargets(_home));

        _files.WriteAllText(configPath, "arbiter: : malformed yaml");
        Assert.Empty(store.ReadGlobalTargets(_home));

        _files.WriteAllText(configPath, "arbiter:\n  global:\n    targets: 12345");
        Assert.Empty(store.ReadGlobalTargets(_home));
    }

    [Fact]
    public void Item9_HostConfigYaml_AlignsUnquotingAndCaseInsensitivity()
    {
        const string initial = """
            arbiter:
              global:
                targets:
                  - 'a''b'
                  - 'Cursor'
            """;

        string merged = HostConfigYaml.WithArbiterGlobalTargets(initial, ["a'b", "cursor", "kilo"]);
        Assert.Contains("- 'kilo'", merged, StringComparison.Ordinal);
        Assert.Single(merged.Split('\n'), line => line.Contains("a''b", StringComparison.Ordinal) || line.Contains("a'b", StringComparison.Ordinal));
        Assert.Single(merged.Split('\n'), line => line.Contains("cursor", StringComparison.OrdinalIgnoreCase));

        string removed = HostConfigYaml.WithoutArbiterGlobalTarget(merged, "CURSOR");
        Assert.DoesNotContain("cursor", removed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Item10_ConfigFileReader_TryLoadUserConfig_NonexistentExplicitPath_FailsNamingExplicitPath()
    {
        string defaultConfig = UserHostConfigPath.Of(_home);
        _files.CreateDirectory(Path.GetDirectoryName(defaultConfig)!);
        _files.WriteAllText(defaultConfig, "arbiter:\n  enabled: true\n");
        FileKyberWeaveConfigRepository repository = new(
            new FakeRepositoryTreeProbe(),
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: _home));

        string customNonexistent = Path.Combine(_home, "custom-does-not-exist.yml");
        KyberWeaveConfigLoadResult result = repository.TryLoadUserConfig(_home, customNonexistent);

        Assert.False(result.Success);
        Assert.Equal(customNonexistent, result.ConfigPath);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Item12_ArbiterConfigResolver_TryResolve_EmptyRepoRoot_FailsCleanly()
    {
        DiagnosticReport report = new();
        using InMemoryArbiterHost host = InMemoryArbiterHost.Create();
        bool success = host.Get<IArbiterConfigResolver>().TryResolve("", null, report, out KyberWeaveConfig _, out string? _);

        Assert.False(success);
        Assert.NotEmpty(report.Items);
        Assert.Contains(report.Items, d => d.Code == "KW-CONFIG-001");
    }

    [Fact]
    public void ReadGlobalTargets_WhenFileThrowsIOException_ReturnsEmptyList()
    {
        string configPath = UserHostConfigPath.Of(_home);
        _files.CreateDirectory(Path.GetDirectoryName(configPath)!);
        _files.WriteAllText(configPath, "arbiter:\n  global:\n    targets:\n      - 'cursor'\n");
        _files.FailWhen(
            operation => operation.Name is "ReadAllText" or "OpenRead" && operation.Path == configPath,
            new IOException("The file is locked by another process."));

        IReadOnlyList<string> targets = new FileUserHostConfigStore(_files).ReadGlobalTargets(_home);

        Assert.Empty(targets);
    }
}
