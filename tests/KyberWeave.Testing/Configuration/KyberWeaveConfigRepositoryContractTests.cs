using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests.Configuration;

/// <summary>
/// The contract every <see cref="IKyberWeaveConfigRepository"/> must satisfy. Run it once against the in-memory fake
/// and once against the repository over the real disk, so a caller that converts to the interface keeps the read,
/// the fallback and the failure result the product has today.
/// </summary>
/// <remarks>
/// A test arranges a repository by writing the file text it would write to disk, through the abstract members below;
/// the suite then asserts only on what the interface returns. Every config file it writes uses
/// <c>task-prompt-budget</c>, whose value distinguishes one file from another without depending on value equality
/// that <see cref="KyberWeaveConfig"/> does not define.
/// </remarks>
public abstract class KyberWeaveConfigRepositoryContractTests : IDisposable
{
    /// <summary>Creates the repository under test.</summary>
    protected abstract IKyberWeaveConfigRepository CreateRepository();

    /// <summary>A fresh repository root that holds nothing.</summary>
    protected abstract string NewRoot();

    /// <summary>A fresh home directory that holds nothing.</summary>
    protected abstract string NewHome();

    /// <summary>Writes a repository's <c>.kyber-weave/kyber-weave.yml</c>.</summary>
    protected abstract void WriteRepositoryConfig(string root, string yaml);

    /// <summary>Writes a legacy repository-root <c>kyber-weave.yml</c>.</summary>
    protected abstract void WriteLegacyConfig(string root, string yaml);

    /// <summary>Writes the per-user config file under <paramref name="home"/>.</summary>
    protected abstract void WriteUserConfig(string home, string yaml);

    /// <summary>Writes <paramref name="yaml"/> at <paramref name="fileName"/> under <paramref name="root"/>.</summary>
    protected abstract void WriteConfigFile(string root, string fileName, string yaml);

    /// <summary>The full path of a repository's <c>.kyber-weave/kyber-weave.yml</c>.</summary>
    protected static string RepositoryConfigPath(string root) => Path.Combine(
        Path.GetFullPath(root),
        KyberWeaveYamlParser.DefaultDirectoryName,
        KyberWeaveYamlParser.DefaultFileName);

    /// <summary>The full path of a legacy repository-root <c>kyber-weave.yml</c>.</summary>
    protected static string LegacyConfigPath(string root) =>
        Path.Combine(Path.GetFullPath(root), KyberWeaveYamlParser.DefaultFileName);

    /// <summary>The full path of the per-user config file under <paramref name="home"/>.</summary>
    protected static string UserConfigPath(string home) => UserHostConfigPath.Of(Path.GetFullPath(home));

    /// <summary>Releases whatever <see cref="NewRoot"/> and <see cref="NewHome"/> created.</summary>
    protected virtual void Cleanup()
    {
    }

    [Fact]
    public void AnEmptyRootLoadsTheProductDefaults()
    {
        string root = NewRoot();
        IKyberWeaveConfigRepository repository = CreateRepository();

        KyberWeaveConfig config = repository.Load(root);
        KyberWeaveConfigLoadResult result = repository.TryLoad(root);

        Assert.Equal(KyberWeaveConfig.DefaultTaskPromptBudget, config.TaskPromptBudget);
        Assert.True(config.StopsOnToolFailure);
        Assert.True(result.Success);
        Assert.Null(result.ConfigPath);
        Assert.Equal(KyberWeaveConfig.DefaultTaskPromptBudget, result.Config!.TaskPromptBudget);
    }

    [Fact]
    public void TheRepositoryConfigFileIsReadAndReported()
    {
        string root = NewRoot();
        WriteRepositoryConfig(root, "task-prompt-budget: 41001\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        KyberWeaveConfig config = repository.Load(root);
        KyberWeaveConfigLoadResult result = repository.TryLoad(root);

        Assert.Equal(41001, config.TaskPromptBudget);
        Assert.True(result.Success);
        Assert.Equal(RepositoryConfigPath(root), result.ConfigPath);
        Assert.Equal(41001, result.Config!.TaskPromptBudget);
    }

    [Fact]
    public void TheLegacyRootFileIsReadWhenTheDefaultOneIsAbsent()
    {
        string root = NewRoot();
        WriteLegacyConfig(root, "task-prompt-budget: 41002\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        KyberWeaveConfig config = repository.Load(root);
        KyberWeaveConfigLoadResult result = repository.TryLoad(root);

        Assert.Equal(41002, config.TaskPromptBudget);
        Assert.Equal(LegacyConfigPath(root), result.ConfigPath);
    }

    [Fact]
    public void TheDefaultFileWinsOverTheLegacyOne()
    {
        string root = NewRoot();
        WriteLegacyConfig(root, "task-prompt-budget: 41002\n");
        WriteRepositoryConfig(root, "task-prompt-budget: 41003\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        Assert.Equal(41003, repository.Load(root).TaskPromptBudget);
        Assert.Equal(RepositoryConfigPath(root), repository.TryLoad(root).ConfigPath);
    }

    [Fact]
    public void AnExplicitRelativeConfigPathIsRead()
    {
        string root = NewRoot();
        WriteRepositoryConfig(root, "task-prompt-budget: 41001\n");
        WriteConfigFile(root, "other.yml", "task-prompt-budget: 41004\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        Assert.Equal(41004, repository.Load(root, "other.yml").TaskPromptBudget);
        Assert.Equal(41004, repository.TryLoad(root, "other.yml").Config!.TaskPromptBudget);
    }

    [Fact]
    public void AnExplicitAbsoluteConfigPathIsRead()
    {
        string root = NewRoot();
        WriteConfigFile(root, "other.yml", "task-prompt-budget: 41005\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        string path = Path.Combine(Path.GetFullPath(root), "other.yml");
        Assert.Equal(41005, repository.Load(root, path).TaskPromptBudget);
        Assert.Equal(41005, repository.TryLoad(root, path).Config!.TaskPromptBudget);
    }

    [Fact]
    public void AnExplicitMissingConfigPathThrowsAndFailsTheAttempt()
    {
        string root = NewRoot();
        IKyberWeaveConfigRepository repository = CreateRepository();

        Assert.Throws<FileNotFoundException>(() => repository.Load(root, "absent.yml"));
        KyberWeaveConfigLoadResult result = repository.TryLoad(root, "absent.yml");

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("absent.yml", result.ConfigPath, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidYamlThrowsOnLoadAndFailsTheAttempt()
    {
        string root = NewRoot();
        WriteRepositoryConfig(root, "task-prompt-budget: not-a-number\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        Assert.ThrowsAny<YamlException>(() => repository.Load(root));
        KyberWeaveConfigLoadResult result = repository.TryLoad(root);

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.NotNull(result.Error);
        Assert.Equal(RepositoryConfigPath(root), result.ConfigPath);
    }

    [Fact]
    public void AConfigTheParserCannotBindFailsTheAttemptRatherThanThrowing()
    {
        string root = NewRoot();
        WriteRepositoryConfig(root, """
            ontology:
              required-keys:
                ~:
                  - audience
            """);
        IKyberWeaveConfigRepository repository = CreateRepository();

        KyberWeaveConfigLoadResult result = repository.TryLoad(root);

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.NotNull(result.Error);
        Assert.Equal(RepositoryConfigPath(root), result.ConfigPath);
    }

    [Fact]
    public void FindConfigPathReportsTheFileAWriterWouldEdit()
    {
        string root = NewRoot();
        string legacyRoot = NewRoot();
        string bothRoot = NewRoot();
        WriteLegacyConfig(legacyRoot, "task-prompt-budget: 41002\n");
        WriteLegacyConfig(bothRoot, "task-prompt-budget: 41002\n");
        WriteRepositoryConfig(bothRoot, "task-prompt-budget: 41003\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        Assert.Null(repository.FindConfigPath(root));
        Assert.Equal(RepositoryConfigPath(bothRoot), repository.FindConfigPath(bothRoot));
        Assert.Equal(LegacyConfigPath(legacyRoot), repository.FindConfigPath(legacyRoot));
    }

    [Fact]
    public void TheUserConfigFileIsReadAsAUserConfig()
    {
        string home = NewHome();
        WriteUserConfig(home, """
            arbiter:
              global:
                targets: [https://example.invalid/acme/repo]
            task-prompt-budget: 41006
            """);
        IKyberWeaveConfigRepository repository = CreateRepository();

        KyberWeaveConfig config = repository.LoadUserConfig(home);
        KyberWeaveConfigLoadResult result = repository.TryLoadUserConfig(home);

        Assert.Equal(41006, config.TaskPromptBudget);
        Assert.Equal(["https://example.invalid/acme/repo"], config.Arbiter.Global.Targets);
        Assert.True(result.Success);
        Assert.Equal(UserConfigPath(home), result.ConfigPath);
        Assert.Equal(["https://example.invalid/acme/repo"], result.Config!.Arbiter.Global.Targets);
    }

    [Fact]
    public void TheSameUserConfigIsRefusedAsARepositoryConfig()
    {
        string root = NewRoot();
        WriteRepositoryConfig(root, """
            arbiter:
              global:
                targets: [https://example.invalid/acme/repo]
            """);
        IKyberWeaveConfigRepository repository = CreateRepository();

        KyberWeaveConfigLoadResult result = repository.TryLoad(root);

        Assert.False(result.Success);
        Assert.Contains("arbiter.global", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentUserConfigLoadsTheProductDefaults()
    {
        string home = NewHome();
        IKyberWeaveConfigRepository repository = CreateRepository();

        KyberWeaveConfig config = repository.LoadUserConfig(home);
        KyberWeaveConfigLoadResult result = repository.TryLoadUserConfig(home);

        Assert.Equal(KyberWeaveConfig.DefaultTaskPromptBudget, config.TaskPromptBudget);
        Assert.True(result.Success);
        Assert.Equal(UserConfigPath(home), result.ConfigPath);
        Assert.Equal(KyberWeaveConfig.DefaultTaskPromptBudget, result.Config!.TaskPromptBudget);
    }

    [Fact]
    public void AnExplicitMissingUserConfigPathThrowsAndFailsTheAttempt()
    {
        string home = NewHome();
        IKyberWeaveConfigRepository repository = CreateRepository();

        Assert.Throws<FileNotFoundException>(() => repository.LoadUserConfig(home, "absent.yml"));
        KyberWeaveConfigLoadResult result = repository.TryLoadUserConfig(home, "absent.yml");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidUserYamlThrowsOnLoadAndFailsTheAttempt()
    {
        string home = NewHome();
        WriteUserConfig(home, "task-prompt-budget: not-a-number\n");
        IKyberWeaveConfigRepository repository = CreateRepository();

        Assert.ThrowsAny<YamlException>(() => repository.LoadUserConfig(home));
        KyberWeaveConfigLoadResult result = repository.TryLoadUserConfig(home);

        Assert.False(result.Success);
        Assert.Equal(UserConfigPath(home), result.ConfigPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }
}
