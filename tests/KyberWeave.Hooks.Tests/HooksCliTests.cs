using KyberWeave.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Hooks.Tests;

public sealed class HooksCliTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("kw-hooks-cli-");
    private readonly StringWriter stdout = new();
    private readonly StringWriter stderr = new();

    public void Dispose()
    {
        stdout.Dispose();
        stderr.Dispose();
        root.Delete(recursive: true);
    }

    private string Repository()
    {
        _ = Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
        File.WriteAllText(Path.Combine(root.FullName, "App.sln"), string.Empty);
        string file = Path.Combine(root.FullName, "Foo.cs");
        File.WriteAllText(file, "class Foo {}");
        return file;
    }

    private string Json(string file) =>
        System.Text.Json.JsonSerializer.Serialize(new { tool_name = "Edit", cwd = root.FullName, tool_input = new { file_path = file } });

    private Task<int> Run(string stdin, ProcessRunResult result, params string[] args) =>
        HooksCli.DispatchAsync(args, new StringReader(stdin), stdout, stderr, services => services.AddSingleton<IProcessRunner>(new StubRunner(result)));

    [Fact]
    public async Task VersionPrintsToStdout()
    {
        int code = await Run(string.Empty, new ProcessRunResult(0, string.Empty, false), "--version");

        Assert.Equal(0, code);
        Assert.Matches(@"^\d+\.\d+\.\d+", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "--harness", "cursor")]
    [InlineData("lint")]
    public async Task UnknownArgumentsPrintUsageAndExitOne(params string[] args)
    {
        int code = await Run(string.Empty, new ProcessRunResult(0, string.Empty, false), args);

        Assert.Equal(1, code);
        Assert.StartsWith("usage:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableStdinExitsZeroWithAWarningInAdditionalContext()
    {
        int code = await Run("{ nope", new ProcessRunResult(1, "x", false), "run", "--harness", "claude");

        Assert.Equal(0, code);
        Assert.Contains("not valid JSON", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("additionalContext", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("not valid JSON", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongHarnessJsonWarnsInsteadOfSkippingSilently()
    {
        int code = await Run("""{"conversation_id":"c1"}""", new ProcessRunResult(1, "x", false), "run", "--harness", "claude");

        Assert.Equal(0, code);
        Assert.Contains("tool_input", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InputWithoutAFileIsSilent()
    {
        int code = await Run("""{"tool_input":{"command":"ls"}}""", new ProcessRunResult(1, "x", false), "run", "--harness", "claude");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task FailingCheckExitsTwoWithFeedbackOnStderrOnly()
    {
        string file = Repository();

        int code = await Run(Json(file), new ProcessRunResult(2, "Foo.cs: bad whitespace", false), "run", "--harness", "claude");

        Assert.Equal(2, code);
        Assert.Contains("bad whitespace", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task PassingCheckIsSilent()
    {
        string file = Repository();

        int code = await Run(Json(file), new ProcessRunResult(0, string.Empty, false), "run", "--harness", "claude");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Equal(string.Empty, stdout.ToString());
    }

    [Fact]
    public async Task MalformedHooksYmlReachesTheAgentThroughAdditionalContext()
    {
        string file = Repository();
        _ = Directory.CreateDirectory(Path.Combine(root.FullName, ".kyber-weave"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, ".kyber-weave", "hooks.yml"), "rules:\n  - id: [unclosed\n");

        int code = await Run(Json(file), new ProcessRunResult(0, string.Empty, false), "run", "--harness", "claude");

        Assert.Equal(0, code);
        Assert.Contains("skipped after an internal error", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("additionalContext", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotesOnAnExitZeroRunAreAlsoPrintedAsAdditionalContextJson()
    {
        string file = Repository();
        _ = Directory.CreateDirectory(Path.Combine(root.FullName, ".kyber-weave"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, ".kyber-weave", "hooks.yml"), "surprise: 1\n");

        int code = await Run(Json(file), new ProcessRunResult(0, string.Empty, false), "run", "--harness", "claude");

        Assert.Equal(0, code);
        Assert.Contains("unknown key 'surprise'", stderr.ToString(), StringComparison.Ordinal);
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        System.Text.Json.JsonElement output = json.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PostToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Contains("unknown key 'surprise'", output.GetProperty("additionalContext").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectDirectoryFromTheEnvironmentLimitsWhichRepositoryIsUsed()
    {
        string file = Repository();
        string elsewhere = Path.Combine(root.FullName, "elsewhere");
        _ = Directory.CreateDirectory(elsewhere);

        int code = await HooksCli.DispatchAsync(
            ["run", "--harness", "claude"],
            new StringReader(Json(file)),
            stdout,
            stderr,
            services => services.AddSingleton<IProcessRunner>(new StubRunner(new ProcessRunResult(2, "bad", false))),
            name => name == "CLAUDE_PROJECT_DIR" ? elsewhere : null);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public async Task ProjectDirectoryFromTheEnvironmentSeamLimitsWhichRepositoryIsUsed()
    {
        string file = Repository();
        string elsewhere = Path.Combine(root.FullName, "elsewhere");
        _ = Directory.CreateDirectory(elsewhere);

        int code = await HooksCli.DispatchAsync(
            ["run", "--harness", "claude"],
            new StringReader(Json(file)),
            stdout,
            stderr,
            services =>
            {
                _ = services.AddSingleton<IProcessRunner>(new StubRunner(new ProcessRunResult(2, "bad", false)));
                _ = services.AddSingleton<IProcessEnvironment>(new StubEnvironment(name => name == "CLAUDE_PROJECT_DIR" ? elsewhere : null));
            });

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr.ToString());
    }

    private sealed class StubEnvironment(Func<string, string?> variables) : IProcessEnvironment
    {
        public string CurrentDirectory => string.Empty;

        public string ApplicationBaseDirectory => string.Empty;

        public string HomeDirectory => string.Empty;

        public string TempDirectory => string.Empty;

        public OperatingSystemKind OperatingSystem => OperatingSystemKind.Unknown;

        public string UserName => string.Empty;

        public string MachineName => string.Empty;

        public int ProcessId => 4242;

        public bool IsFileSystemCaseInsensitive => false;

        public bool IsInputRedirected => true;

        public string? GetVariable(string name) => variables(name);
    }

    private sealed class StubRunner(ProcessRunResult result) : IProcessRunner
    {
        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);

        public Task<ProcessRunResult> RunAsync(IReadOnlyList<string> argv, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public bool IsAlive(int processId) => false;

        public string? FindOnPath(string program) => null;
    }
}
