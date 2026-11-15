using KyberWeave.Application.Hooks;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Hooks;
using Xunit;

namespace KyberWeave.Application.Tests.Hooks;

public sealed class EditHookServiceTests
{
    private static readonly HookEditEvent Edit = new(FakeFileSystem.Abs("src/App/Foo.cs"), FakeFileSystem.Root);

    private readonly FakeClock clock = new();
    private readonly FakeFileSystem fileSystem = new FakeFileSystem()
        .WithDirectory(".git")
        .WithFile("KyberWeave.sln")
        .WithFile("src/App/App.csproj")
        .WithFile("src/App/Foo.cs");

    private HooksConfig config = HooksDefaults.Config;
    private TestClassResolution tests = new(["App.Tests.FooTests"], "tests/App.Tests/App.Tests.csproj");

    private (EditHookService Service, FakeProcessRunner Runner, FakeTestClassResolver Resolver) Build(Exception? configFailure = null)
    {
        FakeProcessRunner runner = new(clock);
        FakeTestClassResolver resolver = new(tests);
        return (new EditHookService(fileSystem, new FakeConfigSource(config, configFailure), resolver, runner, clock), runner, resolver);
    }

    private static HooksConfig Enable(HooksConfig source, string id) =>
        source with { Rules = [.. source.Rules.Select(r => r.Id == id ? r with { Enabled = true } : r)] };

    [Fact]
    public async Task LintFailureExitsTwoWithTrimmedFeedback()
    {
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.Respond = argv => argv.Contains("whitespace")
            ? new ProcessRunResult(2, "Foo.cs(3,1): error WHITESPACE: Fix whitespace formatting.", false)
            : new ProcessRunResult(0, string.Empty, false);

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("[lint] dotnet-format-whitespace: failed (exit 2)", result.Feedback, StringComparison.Ordinal);
        Assert.Contains("Fix whitespace formatting", result.Feedback, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-format-style", result.Feedback, StringComparison.Ordinal);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c => Assert.Equal(FakeFileSystem.Root, c.Cwd));
        Assert.Contains("src/App/App.csproj", runner.Calls[0].Argv);
        Assert.Contains("src/App/Foo.cs", runner.Calls[0].Argv);
    }

    [Fact]
    public async Task SuccessIsSilent()
    {
        (EditHookService service, _, _) = Build();

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(new HookResult(0, string.Empty), result);
    }

    [Fact]
    public async Task NonBlockingFailureIsReportedButExitsZero()
    {
        config = config with { Rules = [.. config.Rules.Select(r => r with { Blocking = false })] };
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.Respond = _ => new ProcessRunResult(1, "style problem", false);

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("style problem", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnmatchedFileRunsNothing()
    {
        _ = fileSystem.WithFile("docs/readme.md");
        (EditHookService service, FakeProcessRunner runner, _) = Build();

        HookResult result = await service.RunAsync(Edit with { FilePath = FakeFileSystem.Abs("docs/readme.md") }, CancellationToken.None);

        Assert.Equal(HookResult.Silent, result);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task FileOutsideAnyRepositoryOrMissingRunsNothing()
    {
        (EditHookService service, FakeProcessRunner runner, _) = Build();

        HookResult missing = await service.RunAsync(Edit with { FilePath = FakeFileSystem.Abs("src/App/Gone.cs") }, CancellationToken.None);
        HookResult outside = await service.RunAsync(Edit with { FilePath = Path.GetFullPath("/elsewhere/x.cs") }, CancellationToken.None);

        Assert.Equal(HookResult.Silent, missing);
        Assert.Equal(HookResult.Silent, outside);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RelativeFilePathIsResolvedAgainstCwd()
    {
        (EditHookService service, FakeProcessRunner runner, _) = Build();

        _ = await service.RunAsync(new HookEditEvent("src/App/Foo.cs", FakeFileSystem.Root), CancellationToken.None);

        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task StopsSpendingTimeOnceTheBudgetIsUsedUp()
    {
        config = Enable(config, "dotnet-test");
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.CostPerRun = TimeSpan.FromSeconds(20);

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(25), runner.Calls[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(10), runner.Calls[1].Timeout);
        Assert.Contains("time budget of 30s used up; 1 check(s) skipped", result.Feedback, StringComparison.Ordinal);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task TimedOutCommandNeverBlocks()
    {
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.Respond = _ => new ProcessRunResult(-1, string.Empty, true);

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("timed out", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableConfigFailsOpen()
    {
        (EditHookService service, FakeProcessRunner runner, _) = Build(new InvalidDataException("bad yaml"));

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("bad yaml", result.Feedback, StringComparison.Ordinal);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task CommandThatCannotStartIsSkippedNotBlocking()
    {
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.Respond = _ => throw new IOException("dotnet not found");

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("could not run", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationFailsOpen()
    {
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.Respond = _ => throw new OperationCanceledException();

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task TestsAreOffByDefaultAndTheResolverIsNotEvenAsked()
    {
        (EditHookService service, FakeProcessRunner runner, FakeTestClassResolver resolver) = Build();

        _ = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, resolver.Calls);
        Assert.DoesNotContain(runner.Calls, c => c.Argv.Contains("test"));
    }

    [Fact]
    public async Task EnabledTestRuleRunsAfterLintOnlyForTheCoveringClasses()
    {
        config = Enable(config, "dotnet-test");
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.Respond = argv => argv.Contains("test") ? new ProcessRunResult(1, "Assert.Equal failed", false) : new ProcessRunResult(0, string.Empty, false);

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(
            ["dotnet", "test", "--project", "tests/App.Tests/App.Tests.csproj", "-c", "Release", "--filter-class", "App.Tests.FooTests"],
            runner.Calls[2].Argv);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("[test] dotnet-test", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledTestRuleIsSkippedWhenNoTestClassCoversTheFile()
    {
        config = Enable(config, "dotnet-test");
        tests = TestClassResolution.None;
        (EditHookService service, FakeProcessRunner runner, _) = Build();

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task StopOnFirstBlockingStopsAfterTheFirstFailure()
    {
        config = config with { StopOnFirstBlocking = true };
        (EditHookService service, FakeProcessRunner runner, _) = Build();
        runner.Respond = _ => new ProcessRunResult(2, "bad", false);

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Single(runner.Calls);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task ConfigWarningsReachTheFeedbackWithoutFailing()
    {
        config = config with { Warnings = ["hooks.yml: unknown key 'oops' ignored."] };
        (EditHookService service, _, _) = Build();

        HookResult result = await service.RunAsync(Edit, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("warning: hooks.yml: unknown key 'oops' ignored.", result.Feedback);
    }
}
