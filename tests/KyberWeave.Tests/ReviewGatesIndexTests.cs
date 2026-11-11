using System.Text.Json;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Fakes;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins that <c>kyber-weave review gates</c>, run over the repository rather than with <c>--agent</c>, makes sure
///     the CodeGraph index of the tree under review exists before any gate starts (rule <c>KW-REVIEW-038</c>).
/// </summary>
/// <remarks>
///     <para>
///         The command takes its CodeGraph child through <see cref="ReviewGatesCommand.Executor" />, the same seam
///         <c>doctor --ensure</c> exposes, so no real <c>codegraph</c> runs. Each gate writes a marker into the
///         repository's git configuration when it starts; a case that must stop before any gate proves it by finding
///         no marker there.
///     </para>
///     <para>
///         The report and the diagnostics are read back from the boundary a caller sees: standard output in
///         <c>--format json</c>, and the file <c>--out</c> writes.
///     </para>
/// </remarks>
public sealed class ReviewGatesIndexTests : IDisposable
{
    private const string IndexRule = "KW-REVIEW-038";
    private const string GateMarkerSection = "kw-index-gate";
    private const string MarkerGate = "repository-wide";

    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public ReviewGatesIndexTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        TestGitRepositoryTemplate.CreateReviewGatesCouncilRepository(_repo);
    }

    private string ReportPath => Path.Combine(_temp.Path, "gates.json");

    public void Dispose()
    {
        _temp.Dispose();
    }

    [Fact]
    public void AGovernedRepositoryWithNoIndexCreatesOneAndRunsItsGates()
    {
        Govern();
        ScriptedProcessExecutor executor = Codegraph();

        CapturedConsoleExecution<int> run = Run(executor);

        Assert.True(run.Result == 0, $"A ready index must let the gates run. {run.Output}");
        Assert.Contains(executor.CallsTo("codegraph"), call => call.ArgumentList.Contains("init", StringComparer.Ordinal));
        JsonElement diagnostic = Assert.Single(Diagnostics(run.Output, IndexRule));
        Assert.Equal("info", SeverityOf(diagnostic), ignoreCase: true);
        string message = diagnostic.GetProperty("message").GetString()!;
        Assert.True(
            message.Contains("created", StringComparison.Ordinal) || message.Contains("synced", StringComparison.Ordinal),
            $"The Info diagnostic must say the index was synced or created. Message: {message}");
        Assert.Contains(Path.GetFullPath(_repo), message, StringComparison.Ordinal);
        Assert.True(GateStarted(), "A ready index must let the repository gates start.");
    }

    [Fact]
    public void AFailedIndexWarnsNamesTheFixAndStillRunsTheGates()
    {
        Govern();

        CapturedConsoleExecution<int> run = Run(Codegraph("init", "index"));

        Assert.True(run.Result == 0, $"A review is read-only, so a failed index must not stop its gates. {run.Output}");
        JsonElement diagnostic = Assert.Single(Diagnostics(run.Output, IndexRule));
        Assert.Equal("warning", SeverityOf(diagnostic), ignoreCase: true);
        string hint = diagnostic.GetProperty("hint").GetString()!;
        Assert.Contains("kyber-weave doctor --ensure", hint, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(_repo), hint, StringComparison.Ordinal);
        Assert.True(GateStarted(), "A warned index must still let the repository gates start.");
    }

    [Fact]
    public void AMissingCodeGraphBinaryWarnsTheSameWay()
    {
        Govern();
        ScriptedProcessExecutor executor = new(startInfo => startInfo.FileName switch
        {
            "git" => new ProcessResult(0, string.Empty, string.Empty),
            "codegraph" => throw DoctorHarness.BinaryNotFound(),
            _ => throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."),
        });

        CapturedConsoleExecution<int> run = Run(executor);

        Assert.True(run.Result == 0, $"A missing binary must not stop a review's gates. {run.Output}");
        Assert.Equal("warning", SeverityOf(Assert.Single(Diagnostics(run.Output, IndexRule))), ignoreCase: true);
        Assert.True(GateStarted(), "A missing binary must still let the repository gates start.");
    }

    [Fact]
    public void AnUngovernedRepositoryRunsItsGatesWithNoIndexDiagnosticAndNoCodeGraphChild()
    {
        WriteRepositoryFile(".kyber-weave/kyber-weave.yml", GateConfiguration(string.Empty));
        ScriptedProcessExecutor executor = Codegraph();

        CapturedConsoleExecution<int> run = Run(executor);

        Assert.True(run.Result == 0, $"An ungoverned repository must run its gates as today. {run.Output}");
        Assert.Empty(Diagnostics(run.Output, IndexRule));
        Assert.Empty(executor.CallsTo("codegraph"));
        Assert.True(GateStarted(), "An ungoverned repository's gates must still start.");
    }

    /// <summary>Writes a governed repository: the host configuration, the marker gate, and the docs folder.</summary>
    private void Govern(string prefix = "")
    {
        WriteRepositoryFile(".kyber-weave/kyber-weave.yml", GateConfiguration(prefix));
        WriteRepositoryFile("docs/README.md", "# docs\n");
    }

    private static string GateConfiguration(string prefix) =>
        prefix
        + "review:\n"
        + "  gates:\n"
        + $"    - id: {MarkerGate}\n"
        + $"      run: [git, config, --local, {GateMarkerSection}.started, yes]\n";

    /// <summary>A CodeGraph that answers healthily, except that each named verb exits 1.</summary>
    private static ScriptedProcessExecutor Codegraph(params string[] failingVerbs) =>
        new(startInfo => startInfo.FileName switch
        {
            "git" => new ProcessResult(0, string.Empty, string.Empty),
            "codegraph" => startInfo.ArgumentList.Any(argument => failingVerbs.Contains(argument, StringComparer.Ordinal))
                ? new ProcessResult(1, string.Empty, "boom")
                : DoctorHarness.Ok(),
            _ => throw new InvalidOperationException($"Unexpected child process: '{startInfo.FileName}'."),
        });

    /// <summary>Runs <c>review gates</c> over the repository with the scripted CodeGraph seam, writing the report to --out.</summary>
    private CapturedConsoleExecution<int> Run(ScriptedProcessExecutor executor)
    {
        CommandApp app = new(new SeamRegistrar(new ReviewGatesCommand { Executor = executor }));
        app.Configure(configuration =>
        {
            configuration.SetApplicationName("kyber-weave");
            configuration.UseStrictParsing();
            configuration.AddBranch("review", review => review.AddCommand<ReviewGatesCommand>("gates"));
        });

        string[] arguments = ["review", "gates", _repo, "--format", "json", "--out", ReportPath];
        return ProcessConsoleCapture.Run(() => app.Run(arguments));
    }

    /// <summary>Whether the marker gate ran, which the gate does by writing its key into the repository's git configuration.</summary>
    private bool GateStarted()
    {
        string config = Path.Combine(_repo, ".git", "config");
        return File.Exists(config) && File.ReadAllText(config).Contains(GateMarkerSection, StringComparison.Ordinal);
    }

    private void WriteRepositoryFile(string relativePath, string content)
    {
        string path = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>The diagnostics in the JSON output whose code matches, read as objects.</summary>
    private static List<JsonElement> Diagnostics(string output, string code)
    {
        char[] opening = ['{', '['];
        char[] closing = ['}', ']'];
        int first = output.IndexOfAny(opening);
        int last = output.LastIndexOfAny(closing);
        Assert.True(first >= 0 && last > first, $"No JSON payload in the output:{Environment.NewLine}{output}");

        using JsonDocument document = JsonDocument.Parse(output[first..(last + 1)]);
        List<JsonElement> found = [];
        CollectDiagnostics(document.RootElement, code, found);
        return found;
    }

    private static void CollectDiagnostics(JsonElement element, string code, List<JsonElement> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                CollectDiagnostics(item, code, found);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("code", out JsonElement foundCode) && foundCode.GetString() == code)
            found.Add(element.Clone());

        foreach (JsonProperty property in element.EnumerateObject())
            CollectDiagnostics(property.Value, code, found);
    }

    private static string SeverityOf(JsonElement diagnostic) =>
        diagnostic.GetProperty("severity").GetString()!;

    /// <summary>Hands the command the test's instance, so the scripted seam reaches it.</summary>
    private sealed class SeamRegistrar(ReviewGatesCommand command) : ITypeRegistrar
    {
        public void Register(Type service, Type implementation)
        {
        }

        public void RegisterInstance(Type service, object instance)
        {
        }

        public void RegisterLazy(Type service, Func<object> factory)
        {
        }

        public ITypeResolver Build() => new SeamResolver(command);
    }

    private sealed class SeamResolver(ReviewGatesCommand command) : ITypeResolver
    {
        public object? Resolve(Type? type)
        {
            if (type is null)
                return null;

            if (type == typeof(ReviewGatesCommand))
                return command;

            if (type == typeof(IAnsiConsole))
                return AnsiConsole.Console;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);

            return !type.IsAbstract && !type.IsInterface && type.GetConstructor(Type.EmptyTypes) is not null
                ? Activator.CreateInstance(type)
                : null;
        }
    }
}
