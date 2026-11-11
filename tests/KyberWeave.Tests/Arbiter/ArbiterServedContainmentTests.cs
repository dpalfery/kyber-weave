using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Sections;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Characterization (task 3.21, should pass now): the served tools and the CLI refuse a path that leaves the repository
/// root, and the served sections tool refuses a document over the size cap. The served comment tool answers a path
/// outside the root with the same error shape as the sections tool.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterServedContainmentTests : IDisposable
{
    private const string OutsideMessageStem = "was not found under the repository root.";

    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _home = new();
    private readonly TempDirectory _outside = new();

    public void Dispose()
    {
        _repo.Dispose();
        _home.Dispose();
        _outside.Dispose();
    }

    /// <summary>The shipped default with no provider: nothing is asked, so only containment and size decide the answer.</summary>
    private sealed class NoProviderAnswers : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArbiterStep1BatchResult(new Dictionary<string, string>(StringComparer.Ordinal), null, null, false));
    }

    private ArbiterTools Tools()
    {
        ArbiterHookDecisionEngine engine = new(
            providerFactory: _ => new NoProviderAnswers(),
            homeDirectory: () => _home.Path);
        return new ArbiterTools(new ArbiterServeContext(
            _repo.Path,
            engine,
            TextWriter.Null,
            Composition.LoadHostConfig,
            _ => true));
    }

    private string WriteOutsideDocument()
    {
        string path = Path.Combine(_outside.Path, "guide.md");
        File.WriteAllText(path, "# Guide\nIntro.\n## Install\nSteps.\n");
        return path;
    }

    private static JsonElement ErrorOnly(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement.Clone();
        Assert.True(root.TryGetProperty("error", out _), $"expected an error answer, got: {json}");
        Assert.False(root.TryGetProperty("items", out _));
        return root;
    }

    /// <summary>A document over the size cap through the served tool is an error answer, not a read of the whole file.</summary>
    [Fact]
    public void AnOversizedDocumentThroughTheSectionsToolIsAnErrorAnswer()
    {
        string path = Path.Combine(_repo.Path, "docs", "big.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Big\n" + new string('a', (int)SectionSelector.MaxDocumentBytes));

        JsonElement root = ErrorOnly(Tools().SelectRelevantDocumentSections("docs/big.md", "Change the install steps."));

        Assert.Contains("larger than", root.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    /// <summary>A sections request for a file outside the root is refused with the containment error, the same wording the comment tool uses.</summary>
    [Fact]
    public void ASectionsRequestOutsideTheRootIsAnErrorAnswer()
    {
        string json = Tools().SelectRelevantDocumentSections(WriteOutsideDocument(), "Change the install steps.");

        string message = ErrorOnly(json).GetProperty("error").GetString() ?? string.Empty;
        Assert.Contains(OutsideMessageStem, message, StringComparison.Ordinal);
    }

    /// <summary>A comment on a file outside the root is refused with the same error shape and wording as the sections tool.</summary>
    [Fact]
    public void ACommentOutsideTheRootHasTheSameErrorShapeAsTheSectionsTool()
    {
        string outside = WriteOutsideDocument();
        ArbiterTools tools = Tools();

        string sections = tools.SelectRelevantDocumentSections(outside, "Change the install steps.");
        string comment = tools.ClassifyBotReviewComment(outside, 2, "Shorten this heading.", null);

        Assert.Equal(
            ErrorOnly(sections).GetProperty("error").GetString(),
            ErrorOnly(comment).GetProperty("error").GetString());
        Assert.Contains(OutsideMessageStem, ErrorOnly(comment).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    /// <summary>The CLI comment command refuses a cited file outside the root: exit 1, the containment message, and no decision.</summary>
    [Fact]
    public void TheCommentCommandRefusesAFileOutsideTheRoot()
    {
        ArbiterCommentCommand command = new();
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new ArbiterCommentSettings
            {
                Root = _repo.Path,
                Path = WriteOutsideDocument(),
                Line = 2,
                Body = "Shorten this heading.",
            }));

        Assert.Equal(1, execution.Result);
        Assert.Contains(OutsideMessageStem, execution.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("decision", execution.Output, StringComparison.OrdinalIgnoreCase);
    }
}
