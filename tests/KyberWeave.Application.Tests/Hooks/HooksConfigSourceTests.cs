using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using KyberWeave.Data.Hooks;
using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Application.Tests.Hooks;

/// <summary>
/// The edit hook's trust model: the repository's file may tune the built-in rules, and only the user's own file may
/// name a command. Failure this guards: a cloned repository ships a <c>run</c> line and an agent's edit executes it.
/// </summary>
public sealed class HooksConfigSourceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("kw-hooks-config-");
    private readonly string _home;
    private readonly string _repository;

    public HooksConfigSourceTests()
    {
        _home = Path.Combine(_root.FullName, "home");
        _repository = Path.Combine(_root.FullName, "repo");
        _ = Directory.CreateDirectory(_home);
        _ = Directory.CreateDirectory(_repository);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private void Write(string root, string yaml)
    {
        string path = HooksConfigPath.User(root) == Path.Combine(root, ".kyber-weave", "hooks.yml")
            ? Path.Combine(root, ".kyber-weave", "hooks.yml")
            : Path.Combine(root, ".kyber-weave", "hooks.yml");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
    }

    private HooksConfig Load() =>
        new YamlHooksConfigSource(new PhysicalFileSystem(), new FakeProcessEnvironment(
                currentDirectory: _home,
                homeDirectory: _home,
                readVariable: name => name is "HOME" or "USERPROFILE" ? _home : null)).Load(_repository);

    [Fact]
    public void ARepositoryRuleThatNamesACommandIsRefusedWithAWarningThatNamesTheUserFile()
    {
        Write(_repository, """
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
        Write(_repository, $$"""
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
        Write(_home, $$"""
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
        Write(_home, """
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
        Write(_repository, """
            rules:
              - id: dotnet-test
                blocking: false
            """);
        Write(_home, """
            rules:
              - id: dotnet-test
                enabled: true
            """);

        HookRule rule = Assert.Single(Load().Rules, rule => rule.Id == "dotnet-test");

        Assert.True(rule.Enabled);
        Assert.False(rule.Blocking);
    }

}
