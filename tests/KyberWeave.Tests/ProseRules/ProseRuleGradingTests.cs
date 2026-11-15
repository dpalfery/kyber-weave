using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Tests.ProseRules;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The model-graded half of the prose checks: Jev reads each shipped document and answers whether it
/// states each catalog rule, and whether each negative control does not. Paid calls, so it runs only
/// when <c>KW_PROSE_GRADE=1</c> and a key resolves; otherwise it skips and says why.
/// </summary>
public sealed class ProseRuleGradingTests
{
    private const string OptInVariable = "KW_PROSE_GRADE";
    private const string EndpointVariable = "KW_PROSE_GRADE_ENDPOINT";
    private const string ModelVariable = "KW_PROSE_GRADE_MODEL";
    private const string RulesVariable = "KW_PROSE_GRADE_RULES";
    private const string OutputVariable = "KW_PROSE_GRADE_OUT";
    private const string CatalogVariable = "KW_PROSE_GRADE_CATALOG";
    private const string DefaultEndpoint = "https://openrouter.ai/api/v1";
    private const string DefaultModel = "typesafe/jev-1.14";

    /// <summary>Stops the run past this many reported input tokens.</summary>
    private const long MaxInputTokens = 12_000_000;

    /// <summary>Stops the run past this reported spend.</summary>
    private const double MaxCostUsd = 2.00;

    private const int Concurrency = 6;

    /// <summary>The size a document is cut into for absence checks, in characters.</summary>
    private const int PartChars = 6_000;
    private const int TimeoutMs = 120_000;

    private static readonly Uri ProbeEndpoint = new("https://grader.invalid/v1");

    [Fact]
    public async Task EveryCatalogRuleIsStatedInItsHomesAndAbsentFromItsControls()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            Assert.Skip($"Makes paid model calls; set {OptInVariable}=1 to run it (key from TYPESAFE_API_KEY or the OS credential store).");
        }

        string endpoint = Environment.GetEnvironmentVariable(EndpointVariable) is { Length: > 0 } configured ? configured : DefaultEndpoint;
        string model = Environment.GetEnvironmentVariable(ModelVariable) is { Length: > 0 } chosen ? chosen : DefaultModel;
        string? key = ResolveKey(endpoint, Environment.GetEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
        {
            Assert.Skip($"{OptInVariable}=1 but no key resolved for {endpoint}: set TYPESAFE_API_KEY or store one for that origin.");
        }

        ProseRuleCatalog catalog = ProseRuleCatalog.Load(
            Environment.GetEnvironmentVariable(CatalogVariable) is { Length: > 0 } catalogPath ? catalogPath : ProseRuleCatalog.DefaultPath());
        IReadOnlyList<ProseRule> rules = SelectRules(catalog, Environment.GetEnvironmentVariable(RulesVariable));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Stopwatch clock = Stopwatch.StartNew();

        using HttpClientHandler transport = new();
        using ProseGrader grader = new(transport, new Uri(endpoint), model, key, catalog.ConfidenceFloor, TimeoutMs);
        List<ProseCase> cases = BuildCases(rules, KyberWeaveTestPaths.ToolRoot, grader.FitsBudget, out List<string> buildErrors);
        Assert.True(buildErrors.Count == 0, "The catalog could not be turned into questions:\n" + string.Join('\n', buildErrors));

        List<ProseResult> results = await GradeAllAsync(grader, cases, cancellationToken);
        clock.Stop();

        string report = ProseReport.Format(results)
            + string.Create(CultureInfo.InvariantCulture,
                $"\n{model} at {endpoint}: {cases.Count} questions over {rules.Count} rules, floor {catalog.ConfidenceFloor}, "
                + $"{grader.InputTokens} input + {grader.OutputTokens} output tokens, ${grader.CostUsd:0.0000}, {clock.Elapsed.TotalSeconds:0}s."
                + $"\n{results.Count(item => item.Outcome == ProseOutcome.Pass)} passed, {results.Count(item => item.Outcome == ProseOutcome.Warning)} warnings "
                + $"(right way, below the floor), {results.Count(item => item.Outcome is ProseOutcome.Wrong or ProseOutcome.Unsure or ProseOutcome.OverBudget)} failed, "
                + $"{results.Count(item => item.Outcome == ProseOutcome.Outage)} unanswered.");
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        await WriteOutputAsync(results, report, cancellationToken);

        int[] refused = grader.FailedStatuses.Where(status => status is 401 or 403).Distinct().ToArray();
        Assert.True(refused.Length == 0, $"The provider refused the key (HTTP {string.Join(", ", refused)}): fix the secret; this is not an outage.");

        List<ProseResult> failures = results.Where(item => item.Outcome is ProseOutcome.Wrong or ProseOutcome.Unsure or ProseOutcome.OverBudget).ToList();
        int outages = results.Count(item => item.Outcome == ProseOutcome.Outage);
        Assert.True(failures.Count == 0, $"{failures.Count} graded checks failed; {outages} got no answer.\n{report}");
        if (outages > 0)
        {
            Assert.Skip($"WARNING: the grader gave no answer to {outages} of {results.Count} questions "
                + $"(HTTP statuses: {string.Join(", ", grader.FailedStatuses.Distinct())}); every answer it did give passed.\n{report}");
        }
    }

    [Fact]
    public void ResolveKey_GivesTheEnvironmentKeyToTheConfiguredEndpoint()
    {
        string? key = ResolveKey(DefaultEndpoint, name => name == ArbiterKeyResolver.EnvVarName ? "test-key" : null, store: null);

        Assert.Equal("test-key", key);
    }

    [Fact]
    public void ResolveKey_ReturnsNoKeyWhenNoneIsSetAndNoStoreAnswers()
    {
        Assert.Null(ResolveKey(DefaultEndpoint, _ => null, store: null));
    }

    [Fact]
    public async Task Grade_ReportsAProviderErrorAsAnOutageNotAFailure()
    {
        using StubHandler handler = new(HttpStatusCode.InternalServerError, "{}");
        using ProseGrader grader = new(handler, ProbeEndpoint, DefaultModel, "key", 0.8, 5_000);

        ProseResult result = await grader.GradeAsync(Case(ProseCaseKind.Stated), TestContext.Current.CancellationToken);

        Assert.Equal(ProseOutcome.Outage, result.Outcome);
    }

    [Fact]
    public async Task Grade_ReportsAConfidentWrongAnswerAsWrong()
    {
        using StubHandler handler = new(HttpStatusCode.OK, Answer(ProseGrader.NotStatedAnswer, 0.95));
        using ProseGrader grader = new(handler, ProbeEndpoint, DefaultModel, "key", 0.8, 5_000);

        ProseResult result = await grader.GradeAsync(Case(ProseCaseKind.Stated), TestContext.Current.CancellationToken);

        Assert.Equal(ProseOutcome.Wrong, result.Outcome);
    }

    [Fact]
    public async Task Grade_ReportsARightAnswerBelowTheFloorAsAWarning()
    {
        using StubHandler handler = new(HttpStatusCode.OK, Answer(ProseGrader.StatedAnswer, 0.6));
        using ProseGrader grader = new(handler, ProbeEndpoint, DefaultModel, "key", 0.8, 5_000);

        ProseResult result = await grader.GradeAsync(Case(ProseCaseKind.Stated), TestContext.Current.CancellationToken);

        Assert.Equal(ProseOutcome.Warning, result.Outcome);
    }

    [Fact]
    public async Task Grade_ReportsAWrongAnswerBelowTheFloorAsUnsure()
    {
        using StubHandler handler = new(HttpStatusCode.OK, Answer(ProseGrader.NotStatedAnswer, 0.6));
        using ProseGrader grader = new(handler, ProbeEndpoint, DefaultModel, "key", 0.8, 5_000);

        ProseResult result = await grader.GradeAsync(Case(ProseCaseKind.Stated), TestContext.Current.CancellationToken);

        Assert.Equal(ProseOutcome.Unsure, result.Outcome);
    }

    [Fact]
    public async Task Grade_CountsTheReportedCost()
    {
        using StubHandler handler = new(HttpStatusCode.OK, Answer(ProseGrader.NotStatedAnswer, 0.9));
        using ProseGrader grader = new(handler, ProbeEndpoint, DefaultModel, "key", 0.8, 5_000);

        ProseResult result = await grader.GradeAsync(Case(ProseCaseKind.Control), TestContext.Current.CancellationToken);

        Assert.Equal(ProseOutcome.Pass, result.Outcome);
        Assert.Equal(0.002, grader.CostUsd, 6);
        Assert.Equal(100, grader.InputTokens);
    }

    [Fact]
    public void BuildCases_SendsTheNamedSectionWhenAWholeDocumentIsOverBudget()
    {
        ProseRule rule = new(
            "T1",
            "Do the thing.",
            [new ProseDocumentRef("AGENTS.md", "The inner loop")],
            [new ProseControl("AGENTS.md", "The inner loop")],
            null);

        List<ProseCase> cases = BuildCases(
            [rule], KyberWeaveTestPaths.ToolRoot, item => item.Kind == ProseCaseKind.Control || item.Text.Length < 4_000, out List<string> errors);

        Assert.Empty(errors);
        ProseCase stated = Assert.Single(cases, item => item.Kind == ProseCaseKind.Stated);
        Assert.StartsWith("### The inner loop", stated.Text, StringComparison.Ordinal);
        Assert.Contains("section 'The inner loop'", stated.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// Turns the catalog into questions. A document over the model's budget is sent as the catalog's named
    /// section, and the note says so; one with no named section, or still over budget, is an error.
    /// </summary>
    internal static List<ProseCase> BuildCases(
        IReadOnlyList<ProseRule> rules, string root, Func<ProseCase, bool> fitsBudget, out List<string> errors)
    {
        List<ProseCase> cases = [];
        List<string> problems = [];
        errors = problems;
        foreach (ProseRule rule in rules)
        {
            foreach (ProseDocumentRef document in rule.StatedIn.Where(item => item.Anchor is null))
            {
                foreach (string path in ProseRuleCatalog.Resolve(root, document))
                {
                    // The whole document is sent: measured against sending only the named section, the section
                    // alone lost context and made the grader less sure. The section is the fallback for budget.
                    Add(rule.Id, ProseCaseKind.Stated, path, rule.Statement, Read(root, path), null, document.Section);
                }
            }

            foreach (ProseControl control in rule.Controls)
            {
                string text = Read(root, control.Path);
                if (control.WithoutSection is { } heading)
                {
                    string? cut = ProseRuleCatalog.WithoutSection(text, heading);
                    if (cut is null)
                    {
                        errors.Add($"{rule.Id}: control {control.Path} has no section '{heading}'.");
                        continue;
                    }

                    Add(rule.Id, ProseCaseKind.Control, control.Path, rule.Statement, cut, $"without section '{heading}'", null);
                }
                else
                {
                    Add(rule.Id, ProseCaseKind.Control, control.Path, rule.Statement, text, null, null);
                }
            }

            if (rule.Retired is not { } retired)
            {
                continue;
            }

            string seedPath = retired.SeedInto ?? (ProseRuleCatalog.Resolve(root, retired.AbsentFrom[0]) is { Count: > 0 } first ? first[0] : retired.AbsentFrom[0].Pattern);
            string seeded = InsertMidway(Read(root, seedPath), retired.Example);
            string seededPart = ProseRuleCatalog.Parts(seeded, PartChars).First(item => item.Contains(retired.Example, StringComparison.Ordinal));
            Add(rule.Id, ProseCaseKind.RetiredControl, seedPath, retired.Statement, seededPart,
                "retired example inserted into the part it lands in", null, rule.Statement);

            foreach (ProseDocumentRef document in retired.AbsentFrom)
            {
                foreach (string path in ProseRuleCatalog.Resolve(root, document))
                {
                    // Absence is judged part by part: a retired sentence can sit anywhere, and a short part keeps
                    // the rest of a long file from diluting the answer.
                    string text = Read(root, path);
                    IReadOnlyList<string> parts = ProseRuleCatalog.Parts(text, PartChars);
                    for (int index = 0; index < parts.Count; index++)
                    {
                        Add(rule.Id, ProseCaseKind.RetiredAbsent, path, retired.Statement, parts[index],
                            parts.Count == 1 ? null : $"part {index + 1} of {parts.Count}", null, rule.Statement);
                    }

                }
            }
        }

        return cases;

        void Add(string id, ProseCaseKind kind, string path, string statement, string text, string? note, string? section, string? contrast = null)
        {
            ProseCase item = new(id, kind, path, statement, text, note, contrast);
            if (fitsBudget(item))
            {
                cases.Add(item);
                return;
            }

            string? part = section is null ? null : ProseRuleCatalog.Section(text, section);
            ProseCase narrowed = item with { Text = part ?? string.Empty, Note = $"section '{section}' only: the whole document is over the model's budget" };
            if (part is null || !fitsBudget(narrowed))
            {
                problems.Add($"{id}: {path} is over the model's budget and names no section that fits.");
                return;
            }

            cases.Add(narrowed);
        }
    }

    internal static string? ResolveKey(string endpoint, Func<string, string?> environment, ICredentialStore? store = null)
    {
        // The environment key serves only the TypeSafe origin unless the endpoint is passed as the
        // override, so an OpenRouter endpoint must name itself here or the key never resolves.
        return ArbiterKeyResolver.Resolve(endpoint, store, environment, userOverrideEndpoint: endpoint);
    }

    private static string? ResolveKey(string endpoint, Func<string, string?> environment)
    {
        ICredentialStore? store = null;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                store = new MacKeychainCredentialStore(new ProcessRunnerCredentialProcessRunner());
            }
            else if (OperatingSystem.IsLinux())
            {
                store = new SecretServiceCredentialStore(new ProcessRunnerCredentialProcessRunner());
            }

            return ResolveKey(endpoint, environment, store);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return ResolveKey(endpoint, environment, store: null);
        }
    }

    private static IReadOnlyList<ProseRule> SelectRules(ProseRuleCatalog catalog, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return catalog.Rules;
        }

        HashSet<string> ids = new(filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        List<string> unknown = ids.Where(id => catalog.Rules.All(rule => rule.Id != id)).ToList();
        Assert.True(unknown.Count == 0, $"{RulesVariable} names rules the catalog does not have: {string.Join(", ", unknown)}");
        return catalog.Rules.Where(rule => ids.Contains(rule.Id)).ToList();
    }

    private static async Task<List<ProseResult>> GradeAllAsync(ProseGrader grader, List<ProseCase> cases, CancellationToken cancellationToken)
    {
        using SemaphoreSlim slots = new(Concurrency);
        Task<ProseResult>[] running = cases.Select(async item =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                if (grader.InputTokens >= MaxInputTokens || grader.CostUsd >= MaxCostUsd)
                {
                    return new ProseResult(item, ProseOutcome.OverBudget, null, null,
                        $"not sent: the run reached its cap ({grader.InputTokens} tokens, ${grader.CostUsd:0.00})");
                }

                return await grader.GradeAsync(item, cancellationToken);
            }
            finally
            {
                slots.Release();
            }
        }).ToArray();
        return [.. await Task.WhenAll(running)];
    }

    private static async Task WriteOutputAsync(List<ProseResult> results, string report, CancellationToken cancellationToken)
    {
        string? output = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        StringBuilder lines = new();
        foreach (ProseResult result in results)
        {
            lines.AppendLine(JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["rule"] = result.Case.RuleId,
                ["kind"] = result.Case.Kind.ToString(),
                ["path"] = result.Case.Path,
                ["expected"] = result.Case.Expected,
                ["answer"] = result.Answer,
                ["confidence"] = result.Confidence,
                ["outcome"] = result.Outcome.ToString(),
                ["note"] = result.Case.Note,
                ["chars"] = result.Case.Text.Length,
            }));
        }

        await File.WriteAllTextAsync(output, lines.ToString(), cancellationToken);
        await File.WriteAllTextAsync(output + ".txt", report, cancellationToken);
    }

    private static string Read(string root, string path) => File.ReadAllText(Path.Combine(root, path));

    /// <summary>Inserts <paramref name="sentence"/> as its own paragraph at the blank line nearest the middle.</summary>
    private static string InsertMidway(string text, string sentence)
    {
        int middle = text.Length / 2;
        int blank = text.IndexOf("\n\n", middle, StringComparison.Ordinal);
        int at = blank < 0 ? text.Length : blank + 1;
        return string.Concat(text.AsSpan(0, at), "\n" + sentence + "\n", text.AsSpan(at));
    }

    private static ProseCase Case(ProseCaseKind kind) =>
        new("T1", kind, "AGENTS.md", "Do the thing.", "Some document.", null);

    private static string Answer(string choice, double confidence) =>
        string.Create(CultureInfo.InvariantCulture,
            $$$"""{"model":"jev","answers":{"rule":{"type":"choice","choice":"{{{choice}}}","confidence":{{{confidence}}}}},"usage":{"input_tokens":100,"output_tokens":2,"cost":0.002}}""");

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
