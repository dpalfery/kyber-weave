using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// What the two served tools added for tasks 3.11 and 3.13 answer when no provider is configured, which is the
/// shipped default. Both fail safe: every section comes back, and a comment is classed <c>real</c>.
/// <c>arbiter_select_relevant_document_sections</c> returns each section's id, title and line range.
/// <c>arbiter_classify_bot_review_comment</c> returns the class, the confidence and the decision id.
/// RED: <c>ArbiterTools.SelectRelevantDocumentSections</c> and <c>ArbiterTools.ClassifyBotReviewComment</c> do not
/// exist yet.
/// </summary>
/// <remarks>
/// The tool names are pinned in <c>McpToolNameContractTests</c> and the allow list in <c>ConductorGuardTests</c>.
/// This file pins the answer shape only.
/// </remarks>
public sealed class ArbiterSectionsAndCommentToolTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _repo.Dispose();
        _home.Dispose();
    }

    /// <summary>The shipped default: no provider, so step-1 rules are never asked.</summary>
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

    private string WriteDocument()
    {
        string path = Path.Combine(_repo.Path, "docs", "guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# Guide\nIntro.\n## Install\nSteps.\n## Usage\nRun it.\n");
        return "docs/guide.md";
    }

    /// <summary>Without a provider every section comes back, with its id and line range, so the worker reads the whole document.</summary>
    [Fact]
    public void SelectingSectionsWithNoProviderReturnsEverySectionWithItsLineRange()
    {
        string path = WriteDocument();

        using JsonDocument json = JsonDocument.Parse(Tools().SelectRelevantDocumentSections(path, "Change the install steps."));

        JsonElement sections = json.RootElement.GetProperty("sections");
        Assert.Equal(3, sections.GetArrayLength());
        Assert.Equal("guide/install", sections[1].GetProperty("id").GetString());
        Assert.Equal("Install", sections[1].GetProperty("title").GetString());
        Assert.Equal(3, sections[1].GetProperty("startLine").GetInt32());
        Assert.Equal(4, sections[1].GetProperty("endLine").GetInt32());
    }

    /// <summary>A document that does not exist is an error the caller sees, not an empty section list.</summary>
    [Fact]
    public void SelectingSectionsOfAMissingDocumentReportsAnError()
    {
        using JsonDocument json = JsonDocument.Parse(Tools().SelectRelevantDocumentSections("docs/missing.md", "Anything."));

        Assert.True(json.RootElement.TryGetProperty("error", out _));
        Assert.False(json.RootElement.TryGetProperty("sections", out _));
    }

    /// <summary>With no provider the comment is classed real, with a decision id the caller can cite.</summary>
    [Fact]
    public void ClassifyingACommentWithNoProviderIsRealWithADecisionId()
    {
        WriteDocument();

        using JsonDocument json = JsonDocument.Parse(Tools().ClassifyBotReviewComment(
            "docs/guide.md", 2, "Shorten this heading.", null));

        Assert.Equal("real", json.RootElement.GetProperty("class").GetString());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("decisionId").GetString()));
    }
}
