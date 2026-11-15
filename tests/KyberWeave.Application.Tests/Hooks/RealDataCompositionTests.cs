using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Domain.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Application.Tests.Hooks;

public sealed class RealDataCompositionTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("kw-hooks-");
    private readonly FakeClock clock = new();

    public void Dispose() => root.Delete(recursive: true);

    private string Write(string relative, string content)
    {
        string path = Path.Combine(root.FullName, relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private ServiceProvider Provider(FakeProcessRunner runner)
    {
        ServiceCollection services = [];
        _ = services.AddSingleton<IProcessRunner>(runner);
        _ = services.AddWeaveApplication();
        return services.BuildServiceProvider();
    }

    private string Repository()
    {
        _ = Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
        Write("App.sln", string.Empty);
        Write("src/App/App.csproj", "<Project />");
        return Write("src/App/Foo.cs", "class Foo {}");
    }

    [Fact]
    public async Task RegistrationKeepsAHostSuppliedRunnerAndRunsTheBuiltInRules()
    {
        string file = Repository();
        FakeProcessRunner runner = new(clock);
        using ServiceProvider provider = Provider(runner);

        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName, "Write"), CancellationToken.None);

        Assert.Equal(HookResult.Silent, result);
        Assert.Equal(["dotnet", "format", "App.sln", "whitespace", "--verify-no-changes", "--include", "src/App/Foo.cs", "-v", "minimal"], runner.Calls[0].Argv);
        Assert.Equal(root.FullName, runner.Calls[0].Cwd);
    }

    [Fact]
    public async Task RepositoryConfigOverridesDefaultsAddsRulesAndWarnsOnUnknownKeys()
    {
        string file = Repository();
        _ = Write(
            ".kyber-weave/hooks.yml",
            """
            time-budget-seconds: 10
            surprise: 1
            rules:
              - id: dotnet-format-style
                enabled: false
              - id: ts-lint
                kind: lint
                include: ["web/**/*.ts"]
                run: [npx, eslint, "{file}"]
                colour: red
            """);
        FakeProcessRunner runner = new(clock);
        using ServiceProvider provider = Provider(runner);

        IHooksConfigSource source = provider.GetRequiredService<IHooksConfigSource>();
        HooksConfig config = source.Load(root.FullName);
        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName, null), CancellationToken.None);

        Assert.Equal(10, config.TimeBudgetSeconds);
        Assert.False(config.Rules.Single(r => r.Id == "dotnet-format-style").Enabled);
        Assert.Equal(["npx", "eslint", "{file}"], config.Rules.Single(r => r.Id == "ts-lint").Run);
        Assert.Contains(config.Warnings, w => w.Contains("unknown key 'surprise'", StringComparison.Ordinal));
        Assert.Contains(config.Warnings, w => w.Contains("rule 'ts-lint'", StringComparison.Ordinal) && w.Contains("'colour'", StringComparison.Ordinal));
        Assert.Single(runner.Calls);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("unknown key 'surprise'", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedConfigFailsOpen()
    {
        string file = Repository();
        _ = Write(".kyber-weave/hooks.yml", "rules: [unclosed");
        FakeProcessRunner runner = new(clock);
        using ServiceProvider provider = Provider(runner);

        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName, null), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ConventionResolverFindsAndVerifiesTheCoveringTestClass()
    {
        string file = Repository();
        _ = Write("tests/App.Tests/App.Tests.csproj", "<Project />");
        _ = Write("tests/App.Tests/FooTests.cs", "namespace App.Tests;\npublic sealed class FooTests {}");
        _ = Write("tests/App.Tests/BarTests.cs", "namespace App.Tests;\npublic sealed class SomethingElse {}");
        _ = Write(".kyber-weave/hooks.yml", "rules:\n  - id: dotnet-test\n    enabled: true\n");
        FakeProcessRunner runner = new(clock);
        using ServiceProvider provider = Provider(runner);

        ITestClassResolver resolver = provider.GetRequiredService<ITestClassResolver>();
        Assert.Equal(new TestClassResolution(["App.Tests.FooTests"], "tests/App.Tests/App.Tests.csproj"), resolver.Resolve(root.FullName, "src/App/Foo.cs"), new ResolutionComparer());
        Assert.Equal(TestClassResolution.None, resolver.Resolve(root.FullName, "src/App/Bar.cs"), new ResolutionComparer());

        _ = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName, null), CancellationToken.None);

        Assert.Equal(
            ["dotnet", "test", "--project", "tests/App.Tests/App.Tests.csproj", "-c", "Release", "--filter-class", "App.Tests.FooTests"],
            runner.Calls[^1].Argv);
    }

    private sealed class ResolutionComparer : IEqualityComparer<TestClassResolution>
    {
        public bool Equals(TestClassResolution? x, TestClassResolution? y) =>
            x is not null && y is not null && x.ProjectPath == y.ProjectPath && x.Classes.SequenceEqual(y.Classes);

        public int GetHashCode(TestClassResolution obj) => obj.ProjectPath?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
