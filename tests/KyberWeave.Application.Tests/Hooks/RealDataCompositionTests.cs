using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
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

    private string UserHome => Path.Combine(root.FullName, "home");

    /// <summary>Writes the user's own hooks file, the only one that may name a command.</summary>
    private void WriteUser(string yaml)
    {
        string path = HooksConfigPath.User(UserHome);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yaml);
    }

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
        _ = services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(
            currentDirectory: UserHome,
            homeDirectory: UserHome,
            readVariable: name => name is "HOME" or "USERPROFILE" ? UserHome : null));
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
    public async Task KyberWeaveDirectoryMarksTheRootOnlyWhenNoGitExistsAbove()
    {
        _ = Directory.CreateDirectory(Path.Combine(root.FullName, ".kyber-weave"));
        _ = Write("src/App/App.csproj", "<Project />");
        string file = Write("src/App/Foo.cs", "class Foo {}");
        FakeProcessRunner runner = new(clock);
        using ServiceProvider provider = Provider(runner);

        _ = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName), CancellationToken.None);

        Assert.Equal(root.FullName, runner.Calls[0].Cwd);
    }

    [Fact]
    public async Task MissingExecutableIsReportedAsCommandNotFoundAndNeverBlocks()
    {
        string file = Repository();
        _ = Write(
            ".kyber-weave/hooks.yml",
            """
            rules:
              - id: dotnet-format-whitespace
                enabled: false
              - id: dotnet-format-style
                enabled: false
            """);
        WriteUser("""
            rules:
              - id: ghost
                kind: lint
                include: ["src/**/*.cs"]
                run: [kyber-weave-no-such-command-xyz, "{file}"]
            """);
        ServiceCollection services = [];
        _ = services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(
            currentDirectory: UserHome,
            homeDirectory: UserHome,
            readVariable: name => name is "HOME" or "USERPROFILE" ? UserHome : null));
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("command not found: kyber-weave-no-such-command-xyz", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationKeepsAHostSuppliedRunnerAndRunsTheBuiltInRules()
    {
        string file = Repository();
        FakeProcessRunner runner = new(clock);
        using ServiceProvider provider = Provider(runner);

        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName), CancellationToken.None);

        Assert.Equal(HookResult.Silent, result);
        Assert.Equal(["dotnet", "format", "src/App/App.csproj", "whitespace", "--verify-no-changes", "--include", "src/App/Foo.cs", "-v", "minimal"], runner.Calls[0].Argv);
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
            """);
        WriteUser("""
            rules:
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
        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName), CancellationToken.None);

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
    public void WrongValueTypesWarnAndFallBackToTheDefaults()
    {
        _ = Repository();
        WriteUser(
            """
            time-budget-seconds: soon
            stop-on-first-blocking: maybe
            rules:
              - just-a-string
              - id: dotnet-format-style
                kind: 5
                include: "**/*.cs"
                timeout-seconds: 0
                blocking: nope
              - id: Lowercase-kind
                kind: LINT
                include: ["**/*.ts"]
                run: [tool]
            """);
        using ServiceProvider provider = Provider(new FakeProcessRunner(clock));

        HooksConfig config = provider.GetRequiredService<IHooksConfigSource>().Load(root.FullName);

        Assert.Equal(HooksDefaults.Config.TimeBudgetSeconds, config.TimeBudgetSeconds);
        Assert.Equal(HooksDefaults.Config.StopOnFirstBlocking, config.StopOnFirstBlocking);
        HookRule style = config.Rules.Single(r => r.Id == "dotnet-format-style");
        Assert.Equal(HookRuleKind.Lint, style.Kind);
        Assert.Equal(25, style.TimeoutSeconds);
        Assert.True(style.Blocking);
        Assert.Equal(HookRuleKind.Lint, config.Rules.Single(r => r.Id == "Lowercase-kind").Kind);
        Assert.Contains(config.Warnings, w => w.Contains("not a mapping", StringComparison.Ordinal));
        Assert.Contains(config.Warnings, w => w.Contains("kind must be", StringComparison.Ordinal));
        Assert.Contains(config.Warnings, w => w.Contains("'include' must be a list", StringComparison.Ordinal));
        Assert.Contains(config.Warnings, w => w.Contains("'timeout-seconds' must be a positive whole number", StringComparison.Ordinal));
        Assert.Contains(config.Warnings, w => w.Contains("'blocking' must be true or false", StringComparison.Ordinal));
        Assert.Contains(config.Warnings, w => w.Contains("'time-budget-seconds' must be a positive whole number", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("include: [{a: b}]")]
    [InlineData("include: [[\"**/*.ts\"]]")]
    [InlineData("exclude: [null]")]
    [InlineData("run: [tool, {x: y}]")]
    public void NonScalarListItemRejectsTheRuleWithAWarning(string field)
    {
        _ = Repository();
        WriteUser(
            $"rules:\n  - id: bad-rule\n    kind: lint\n    include: [\"**/*.ts\"]\n    run: [tool]\n    {field}\n  - id: good-rule\n    kind: lint\n    include: [\"**/*.ts\"]\n    run: [tool]\n");
        using ServiceProvider provider = Provider(new FakeProcessRunner(clock));

        HooksConfig config = provider.GetRequiredService<IHooksConfigSource>().Load(root.FullName);

        Assert.DoesNotContain(config.Rules, r => r.Id == "bad-rule");
        Assert.Contains(config.Rules, r => r.Id == "good-rule");
        Assert.Contains(config.Warnings, w => w.Contains("rule 'bad-rule'", StringComparison.Ordinal) && w.Contains("plain values", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedConfigFailsOpen()
    {
        string file = Repository();
        _ = Write(".kyber-weave/hooks.yml", "rules: [unclosed");
        FakeProcessRunner runner = new(clock);
        using ServiceProvider provider = Provider(runner);

        HookResult result = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName), CancellationToken.None);

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

        _ = await provider.GetRequiredService<IEditHookService>().RunAsync(new HookEditEvent(file, root.FullName), CancellationToken.None);

        Assert.Equal(
            ["dotnet", "test", "--project", "tests/App.Tests/App.Tests.csproj", "-c", "Release", "--filter-class", "App.Tests.FooTests"],
            runner.Calls[^1].Argv);
    }

    [Theory]
    [InlineData("// class FooTests is still to be written\npublic sealed class Other {}")]
    [InlineData("public sealed class Other { string s = \"class FooTests\"; }")]
    [InlineData("/*\n  class FooTests {}\n*/\npublic sealed class Other {}")]
    public void ConventionResolverIgnoresClassNamesInCommentsAndStrings(string source)
    {
        _ = Write("tests/App.Tests/App.Tests.csproj", "<Project />");
        _ = Write("tests/App.Tests/FooTests.cs", "namespace App.Tests;\n" + source);
        using ServiceProvider provider = Provider(new FakeProcessRunner(clock));

        Assert.Equal(TestClassResolution.None, provider.GetRequiredService<ITestClassResolver>().Resolve(root.FullName, "src/App/Foo.cs"), new ResolutionComparer());
    }

    [Theory]
    [InlineData("/* namespace Fake; */\nnamespace Real;\npublic sealed class FooTests {}")]
    [InlineData("/*\nnamespace Fake;\n*/\nnamespace Real;\npublic sealed class FooTests {}")]
    public void ConventionResolverReadsTheNamespaceOutsideBlockComments(string source)
    {
        _ = Write("tests/App.Tests/App.Tests.csproj", "<Project />");
        _ = Write("tests/App.Tests/FooTests.cs", source);
        using ServiceProvider provider = Provider(new FakeProcessRunner(clock));

        Assert.Equal(
            new TestClassResolution(["Real.FooTests"], "tests/App.Tests/App.Tests.csproj"),
            provider.GetRequiredService<ITestClassResolver>().Resolve(root.FullName, "src/App/Foo.cs"),
            new ResolutionComparer());
    }

    [Fact]
    public void ConventionResolverTreatsWildcardCharactersInTheFileNameAsPlainText()
    {
        _ = Write("tests/App.Tests/App.Tests.csproj", "<Project />");
        _ = Write("tests/App.Tests/Foo1Tests.cs", "namespace App.Tests;\npublic sealed class Foo1Tests {}");
        using ServiceProvider provider = Provider(new FakeProcessRunner(clock));

        TestClassResolution result = provider.GetRequiredService<ITestClassResolver>().Resolve(root.FullName, "src/App/Foo[1].cs");

        Assert.Equal(TestClassResolution.None, result, new ResolutionComparer());
    }

    [Fact]
    public void SameNamedClassInTwoProjectsKeepsTheFirstProjectAndSaysTheOtherWasNotRun()
    {
        _ = Repository();
        _ = Write("tests/B.Tests/B.Tests.csproj", "<Project />");
        _ = Write("tests/B.Tests/FooTests.cs", "namespace B.Tests;\npublic sealed class FooTests {}");
        _ = Write("tests/A.Tests/A.Tests.csproj", "<Project />");
        _ = Write("tests/A.Tests/FooTests.cs", "namespace A.Tests;\npublic sealed class FooTests {}");
        using ServiceProvider provider = Provider(new FakeProcessRunner(clock));

        TestClassResolution result = provider.GetRequiredService<ITestClassResolver>().Resolve(root.FullName, "src/App/Foo.cs");

        Assert.Equal("tests/A.Tests/A.Tests.csproj", result.ProjectPath);
        Assert.Equal(["A.Tests.FooTests"], result.Classes);
        Assert.Contains("tests/B.Tests/B.Tests.csproj", result.Note, StringComparison.Ordinal);
        Assert.Contains("not run", result.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableTestFileIsSkippedInsteadOfFailingTheResolution()
    {
        FakeFileSystem files = new FakeFileSystem()
            .WithFile("tests/A.Tests/A.Tests.csproj")
            .WithUnreadableFile("tests/A.Tests/FooTests.cs")
            .WithFile("tests/B.Tests/B.Tests.csproj")
            .WithFile("tests/B.Tests/FooTests.cs", "namespace B.Tests;\npublic sealed class FooTests {}");
        ServiceCollection services = [];
        _ = services.AddSingleton<IFileSystem>(files);
        _ = services.AddWeaveApplication();
        using ServiceProvider provider = services.BuildServiceProvider();

        TestClassResolution result = provider.GetRequiredService<ITestClassResolver>().Resolve(FakeFileSystem.Root, "src/App/Foo.cs");

        Assert.Equal("tests/B.Tests/B.Tests.csproj", result.ProjectPath);
        Assert.Equal(["B.Tests.FooTests"], result.Classes);
        Assert.Null(result.Note);
    }

    private sealed class ResolutionComparer : IEqualityComparer<TestClassResolution>
    {
        public bool Equals(TestClassResolution? x, TestClassResolution? y) =>
            x is not null && y is not null && x.ProjectPath == y.ProjectPath && x.Classes.SequenceEqual(y.Classes);

        public int GetHashCode(TestClassResolution obj) => obj.ProjectPath?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}

