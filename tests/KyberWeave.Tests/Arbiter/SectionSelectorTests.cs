using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Sections;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.11 (section trimming): <c>SectionSelector</c> builds the candidate sections of one document
/// and asks the provider, through <c>KW-ARB-TRIM-001</c>, which of them are relevant to a task. The title section is
/// always kept. The selector fails safe: when it is unsure, the provider did not answer, or the provider failed, it
/// returns every section, which is what the worker gets today.
/// RED: <c>SectionSelector</c> and <c>SectionSelection</c> do not exist yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>new SectionSelector(IArbiterProvider provider)</c>;
/// <c>Task&lt;SectionSelection&gt; SelectAsync(string documentPath, string task, CancellationToken ct = default)</c>;
/// <c>SectionSelection(IReadOnlyList&lt;DocumentSection&gt; Selected, bool FailedSafe)</c>. The answers are
/// <c>relevant</c> and <c>not-relevant</c>; the floor is the rule's own 0.9.
/// </remarks>
public sealed class SectionSelectorTests : IDisposable
{
    private const string Relevant = "relevant";
    private const string NotRelevant = "not-relevant";

    private const string Markdown = """
        # Guide
        Intro.
        ## Install
        Steps.
        ## Usage
        Run it.
        """;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kw-arbiter-sections-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Answers every trim question with one answer at one confidence. <paramref name="requestSent"/> <c>false</c> models
    /// a provider that did not answer; <paramref name="throws"/> models a transport failure.
    /// </summary>
    private sealed class ScriptedSectionProvider(string answer, double confidence, bool requestSent = true, bool throws = false)
        : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            if (throws)
            {
                throw new HttpRequestException("provider unreachable");
            }

            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null)
                .DistinctBy(rule => rule.Id)
                .ToDictionary(rule => rule.Id, _ => answer, StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = answers.Keys
                .ToDictionary(id => id, _ => new ArbiterStep1Score(null, confidence, 0.9), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, requestSent, scores));
        }
    }

    private string WriteDocument()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "guide.md");
        File.WriteAllText(path, Markdown);
        return path;
    }

    /// <summary>When the provider marks every section not relevant, only the title section remains.</summary>
    [Fact]
    public async Task NotRelevantEverywhereKeepsOnlyTheTitleSection()
    {
        SectionSelection selection = await new SectionSelector(new ScriptedSectionProvider(NotRelevant, 0.95))
            .SelectAsync(WriteDocument(), "Change the install steps.");

        Assert.False(selection.FailedSafe);
        Assert.Equal(["guide"], selection.Selected.Select(section => section.Id));
    }

    /// <summary>A relevant answer for every section keeps every section, and that is not a fail-safe.</summary>
    [Fact]
    public async Task RelevantEverywhereKeepsEverySectionWithoutFailingSafe()
    {
        SectionSelection selection = await new SectionSelector(new ScriptedSectionProvider(Relevant, 0.95))
            .SelectAsync(WriteDocument(), "Change the install steps.");

        Assert.False(selection.FailedSafe);
        Assert.Equal(["guide", "guide/install", "guide/usage"], selection.Selected.Select(section => section.Id));
    }

    /// <summary>
    /// A not-relevant answer below the 0.9 floor is not trusted. Failure this guards: a shaky answer trims a section
    /// the worker needed, and the worker then works from an incomplete document.
    /// </summary>
    [Fact]
    public async Task AnAnswerBelowTheFloorFailsSafeToEverySection()
    {
        SectionSelection selection = await new SectionSelector(new ScriptedSectionProvider(NotRelevant, 0.5))
            .SelectAsync(WriteDocument(), "Change the install steps.");

        Assert.True(selection.FailedSafe);
        Assert.Equal(3, selection.Selected.Count);
    }

    /// <summary>A provider that did not send the request has answered nothing, so every section is kept.</summary>
    [Fact]
    public async Task AProviderThatDidNotSendTheRequestFailsSafeToEverySection()
    {
        SectionSelection selection = await new SectionSelector(
                new ScriptedSectionProvider(NotRelevant, 0.95, requestSent: false))
            .SelectAsync(WriteDocument(), "Change the install steps.");

        Assert.True(selection.FailedSafe);
        Assert.Equal(3, selection.Selected.Count);
    }

    /// <summary>A transport failure during the call is a provider failure: every section is kept, and the call does not throw to the worker.</summary>
    [Fact]
    public async Task AProviderFailureFailsSafeToEverySection()
    {
        SectionSelection selection = await new SectionSelector(
                new ScriptedSectionProvider(NotRelevant, 0.95, throws: true))
            .SelectAsync(WriteDocument(), "Change the install steps.");

        Assert.True(selection.FailedSafe);
        Assert.Equal(3, selection.Selected.Count);
    }
}
