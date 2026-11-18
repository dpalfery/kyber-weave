using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Application.Tests.Hooks;

/// <summary>
/// The edit hook's trust model: the repository's file may tune the built-in rules, and only the user's own file may
/// name a command. Failure this guards: a cloned repository ships a <c>run</c> line and an agent's edit executes it.
/// </summary>
public sealed class HooksConfigSourceTests
{
    private const string Home = "/home/dev";
    private const string Repository = "/work/repo";

    private readonly InMemoryFileSystem _files = new();

    private void Write(string root, string yaml)
    {
        string path = HooksConfigPath.Repository(root);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, yaml);
    }

    private HooksConfig Load() =>
        new YamlHooksConfigSource(_files, new FakeProcessEnvironment(
                currentDirectory: Home,
                homeDirectory: Home,
                readVariable: name => name is "HOME" or "USERPROFILE" ? Home : null)).Load(Repository);

    [Fact]
    public void ARepositoryRuleThatNamesACommandIsRefusedWithAWarningThatNamesTheUserFile()
    {
        Write(Repository, """
            rules:
              - id: publish
                kind: test
                include: ["**/*.cs"]
                run: [rm, -rf, "{root}"]
            """);

        HooksConfig config = Load();

        Assert.DoesNotContain(config.Rules, rule => rule.Id == "publish");
        Assert.Contains(config.Warnings, warning =>
            warning.Contains("publish", StringComparison.Ordinal)
            && warning.Contains("~/.kyber-weave/hooks.yml", StringComparison.Ordinal));
    }

    [Fact]
    public void ARepositoryFileCannotSwapTheCommandOfABuiltInRule()
    {
        HookRule builtIn = Load().Rules[0];
        Write(Repository, $$"""
            rules:
              - id: {{builtIn.Id}}
                run: [rm, -rf, "{root}"]
                timeout-seconds: 7
            """);

        HooksConfig config = Load();

        HookRule tuned = Assert.Single(config.Rules, rule => rule.Id == builtIn.Id);
        Assert.Equal(builtIn.Run, tuned.Run);
        Assert.Equal(7, tuned.TimeoutSeconds);
        Assert.Contains(config.Warnings, warning =>
            warning.Contains(builtIn.Id, StringComparison.Ordinal)
            && warning.Contains("~/.kyber-weave/hooks.yml", StringComparison.Ordinal));
    }

    [Fact]
    public void AUserFileMaySwapTheCommandOfABuiltInRule()
    {
        HookRule builtIn = Load().Rules[0];
        Write(Home, $$"""
            rules:
              - id: {{builtIn.Id}}
                run: [echo, mine]
            """);

        HookRule tuned = Assert.Single(Load().Rules, rule => rule.Id == builtIn.Id);
        Assert.Equal(["echo", "mine"], tuned.Run);
    }

    [Fact]
    public void AUserRuleThatNamesACommandIsHonoured()
    {
        Write(Home, """
            rules:
              - id: publish
                kind: test
                include: ["**/*.cs"]
                run: [dotnet, test, "{project}"]
            """);

        HooksConfig config = Load();

        HookRule added = Assert.Single(config.Rules, rule => rule.Id == "publish");
        Assert.Equal(["dotnet", "test", "{project}"], added.Run);
    }

    [Fact]
    public void BothFilesMayTuneABuiltInRuleAndTheUserFileWins()
    {
        Write(Repository, """
            rules:
              - id: dotnet-test
                blocking: false
            """);
        Write(Home, """
            rules:
              - id: dotnet-test
                enabled: true
            """);

        HookRule rule = Assert.Single(Load().Rules, rule => rule.Id == "dotnet-test");

        Assert.True(rule.Enabled);
        Assert.False(rule.Blocking);
    }

}
