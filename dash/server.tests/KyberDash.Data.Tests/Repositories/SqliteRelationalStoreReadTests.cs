using KyberDash.Core.Exceptions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Resilience;
using KyberDash.Data.Collector;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Data.Tests.Repositories;

/// <summary>
/// Parity and fail-closed read tests for <see cref="SqliteRelationalStore"/> (<c>SqliteRelationalStore.Reader.cs</c>)
/// verifying identical behavior against <see cref="InMemoryRelationalStore"/> across empty, unfiltered, and
/// filtered queries via the <see cref="KyberDash.Core.Repositories.BaseSqliteRepository.ExecuteSqliteCore"/> seam.
/// </summary>
/// <remarks>
/// The populated-corpus spot checks read <see cref="CanonPricing"/>'s process-wide tables through the
/// <see langword="null"/>-pricing overloads, so this class joins <see cref="CanonPricingStatics"/>: any
/// parallel class that reconfigures the provider mid-read would otherwise price the same corpus
/// differently between the two stores (or between the parity section and the spot checks).
/// </remarks>
[Collection(CanonPricingStatics.Name)]
public class SqliteRelationalStoreReadTests : IDisposable
{
    private const string DummySha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Verifies an empty <see cref="SqliteRelationalStore"/> matches an empty
    /// <see cref="InMemoryRelationalStore"/> across all 14 <see cref="IProjectionStore"/> reads,
    /// all 18 <see cref="IRelationalStoreReader"/> row queries, the stored-fields inventory and
    /// per-table rows, and <c>DD-13</c> read-time aggregates.
    /// </summary>
    [Fact]
    public void EmptyStore_MatchesInMemoryRelationalStoreAcrossAllQueries()
    {
        string dbPath = Path.Combine(directory.Path, "empty", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        sqlite.ClearRecordedSql();

        AssertQueryParity(sqlite, memory, sessionKeys: ["sess-missing"], harnessIds: [null, "claude-code"], runIds: ["run-missing"], findingIds: ["find-missing"], spanIds: ["span-missing"]);
        Assert.NotEmpty(sqlite.ExecutedSql);
        AssertAllReadScriptsUseTabbedHexWithoutJson(sqlite.ExecutedSql);
    }

    /// <summary>
    /// Verifies <see cref="IRelationalStoreReader.SessionAttributions"/> over the SQLite adapter:
    /// one grouped query over <c>record</c> joined to <c>record_session_link</c>, returning
    /// session keys, raw harness ids, and exact record counts ordered by session key then
    /// harness — the order attribution-driven session-id claiming depends on — and matching
    /// <see cref="InMemoryRelationalStore"/> row for row. An empty store returns empty.
    /// </summary>
    [Fact]
    public void SessionAttributionsMatchTheInMemoryStore()
    {
        string emptyPath = Path.Combine(directory.Path, "attributions-empty", "canon.db");
        SeamSqliteRelationalStore emptySqlite = new(emptyPath, new SqliteCircuitBreaker());
        Assert.Empty(emptySqlite.SessionAttributions());

        string dbPath = Path.Combine(directory.Path, "attributions", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        WriteAttributionTurn(sqlite, memory, "span-alpha-1", "alpha", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-alpha-2", "alpha", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-shared-1", "shared", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-shared-2", "shared", "codex");
        WriteAttributionTurn(sqlite, memory, "span-zeta-1", "zeta", "codex");

        Assert.Equal(
            new StoredSessionAttribution[]
            {
                new("alpha", "claude-code", 2),
                new("shared", "claude-code", 1),
                new("shared", "codex", 1),
                new("zeta", "codex", 1),
            },
            sqlite.SessionAttributions());
        Assert.Equal(memory.SessionAttributions(), sqlite.SessionAttributions());
    }

    /// <summary>
    /// <see cref="IRelationalStoreReader.RecordContentFree"/> answers the span inspector from one
    /// span-scoped script: it never reads the span's whole session, and a record with no session
    /// link still answers its metadata instead of reading as absent.
    /// </summary>
    [Fact]
    public void RecordContentFreeReadsOneSpanWithoutReadingItsSession()
    {
        string dbPath = Path.Combine(directory.Path, "span-content-free", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        WriteAttributionTurn(sqlite, memory, "span-alpha-1", "alpha", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-alpha-2", "alpha", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-beta-1", "beta", "codex");

        sqlite.ClearRecordedSql();
        CollectedRecord? single = sqlite.RecordContentFree("span-alpha-2");

        Assert.NotNull(single);
        Assert.Equal("span-alpha-2", single!.RecordId);
        Assert.Equal("alpha", single.SessionKey);
        Assert.Equivalent(memory.RecordContentFree("span-alpha-2"), single, strict: true);
        string script = Assert.Single(sqlite.ExecutedSql);
        // The scope is the span, encoded as the injection-proof CAST(X'...' AS TEXT) literal the
        // data layer builds for every text predicate.
        string spanLiteral = "CAST(X'" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes("span-alpha-2")) + "' AS TEXT)";
        Assert.Contains(spanLiteral, script, StringComparison.Ordinal);
        Assert.DoesNotContain("session_key IN", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The budgeted turn-content read keeps exactly the rows the in-memory cut kept — the parts
    /// up to and including the one that crosses the byte budget, and nothing after it — matched
    /// row for row against <see cref="InMemoryRelationalStore"/> and against the prefix of the
    /// uncut <c>full</c> read.
    /// </summary>
    [Fact]
    public void TurnContentPartsBudgetCutKeepsTheCrossingRowAndMatchesTheInMemoryCut()
    {
        string dbPath = Path.Combine(directory.Path, "turn-content-budget", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        WriteBudgetedContentTurn(sqlite, memory);

        IReadOnlyList<StoredContentPart> budgeted = sqlite.TurnContentParts("sess-budget", budget: 8);
        IReadOnlyList<StoredContentPart> whole = sqlite.TurnContentParts("sess-budget", full: true);

        // "aaaaa" is 5 bytes and "bbbb" adds 4, crossing the 8-byte budget on the second part,
        // so the cut keeps both and drops "cc" and "dddddd".
        Assert.Equal(["aaaaa", "bbbb"], budgeted.Select(static part => part.Part.PlainText));
        Assert.Equal(4, whole.Count);
        Assert.Equal(whole.Take(2).ToList(), budgeted);
        Assert.Equal(memory.TurnContentParts("sess-budget", budget: 8), budgeted);
    }

    /// <summary>
    /// The budget cut happens inside SQLite, not after the read: the scoped script totals the
    /// bytes before each row and filters on that total, and the text of parts beyond the budget
    /// never crosses the <c>ExecuteSqliteCore</c> seam into managed memory.
    /// </summary>
    [Fact]
    public void TurnContentPartsBudgetCutHappensInSqliteSoTextBeyondTheBudgetIsNeverRead()
    {
        string dbPath = Path.Combine(directory.Path, "turn-content-cut", "canon.db");
        CapturingSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        WriteBudgetedContentTurn(sqlite, memory);
        sqlite.ClearCapturedScripts();

        IReadOnlyList<StoredContentPart> budgeted = sqlite.TurnContentParts("sess-budget", budget: 8);

        Assert.Equal(["aaaaa", "bbbb"], budgeted.Select(static part => part.Part.PlainText));

        (string Script, string Output) contentRead = Assert.Single(sqlite.CapturedScripts);
        Assert.Contains("bytes_before < 8", contentRead.Script, StringComparison.Ordinal);
        Assert.Contains("ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING", contentRead.Script, StringComparison.Ordinal);
        Assert.Contains(ToHex("aaaaa"), contentRead.Output, StringComparison.Ordinal);
        Assert.Contains(ToHex("bbbb"), contentRead.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ToHex("cc"), contentRead.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ToHex("dddddd"), contentRead.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The turn-content count matches <see cref="InMemoryRelationalStore"/> for every scope —
    /// session, turn index, span, and part — and answers an unresolvable scope with zero the way
    /// the parts read answers empty. The script counts rows only: no content-text column is
    /// selected, so no part text crosses the <c>ExecuteSqliteCore</c> seam.
    /// </summary>
    [Fact]
    public void TurnContentPartCountMatchesTheInMemoryStoreAndReadsNoText()
    {
        string dbPath = Path.Combine(directory.Path, "turn-content-count", "canon.db");
        CapturingSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        WriteBudgetedContentTurn(sqlite, memory);
        sqlite.ClearCapturedScripts();

        Assert.Equal(4, sqlite.TurnContentPartCount("sess-budget"));
        Assert.Equal(memory.TurnContentPartCount("sess-budget"), sqlite.TurnContentPartCount("sess-budget"));
        Assert.Equal(
            memory.TurnContentPartCount("sess-budget", turnIndex: 0),
            sqlite.TurnContentPartCount("sess-budget", turnIndex: 0));
        Assert.Equal(
            memory.TurnContentPartCount("sess-budget", spanId: "span-budget-1"),
            sqlite.TurnContentPartCount("sess-budget", spanId: "span-budget-1"));
        Assert.Equal(
            memory.TurnContentPartCount("sess-budget", part: "user_prompt"),
            sqlite.TurnContentPartCount("sess-budget", part: "user_prompt"));

        // An unresolvable scope answers zero the way the parts read answers empty.
        Assert.Equal(0, sqlite.TurnContentPartCount("sess-budget", turnIndex: 9));
        Assert.Equal(0, sqlite.TurnContentPartCount("sess-missing"));

        // One session-scoped count call, captured alone: the script selects COUNT(*) over the
        // scoped rows only — no content-text column, so no part text crosses the seam.
        sqlite.ClearCapturedScripts();
        Assert.Equal(4, sqlite.TurnContentPartCount("sess-budget"));
        (string Script, string Output) countRead = Assert.Single(sqlite.CapturedScripts);
        Assert.Contains("COUNT(*)", countRead.Script, StringComparison.Ordinal);
        Assert.DoesNotContain("part_text", countRead.Script, StringComparison.Ordinal);
        Assert.DoesNotContain(ToHex("aaaaa"), countRead.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reported input total is the checksum of fresh, cache-read, and cache-write tokens.
    /// It is not billed at the fresh rate, and a side the harness did not report is not
    /// billed as zero. A reported output total is one rate and is billed. A class breakdown
    /// that matches the reported total is priced; a remainder is a partial price of the
    /// classes that were stored. Each figure is checked on the SQLite reader and the
    /// in-memory store separately.
    /// </summary>
    [Fact]
    public void ReportedTotals_AreNotBilledAsFreshInputOrAsAMissingZero()
    {
        const double inputRate = 0.000003;
        const double outputRate = 0.000015;
        string dbPath = Path.Combine(directory.Path, "reported-only", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        IModelPricingLookup pricing = new OverridePricingLookup(
            "claude-sonnet-4-6",
            new ModelCosts(
                InputCostPerToken: inputRate,
                OutputCostPerToken: outputRate,
                CacheWriteCostPerToken: 0.00000375,
                CacheReadCostPerToken: 0.0000003,
                WebSearchCostPerRequest: 0,
                FastMultiplier: 1,
                CacheWriteCostIsExplicit: true));

        WritePricedTurn(sqlite, memory, "sess-absent", tokens: [], reportedInput: null, reportedOutput: null);
        WritePricedTurn(
            sqlite,
            memory,
            "sess-zero",
            tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 0),
                new CollectedTokenCount(TokenClass.Output, 0),
            ],
            reportedInput: null,
            reportedOutput: null);
        WritePricedTurn(sqlite, memory, "sess-reported-both", tokens: [], reportedInput: 1_000, reportedOutput: 100);
        WritePricedTurn(sqlite, memory, "sess-reported-input", tokens: [], reportedInput: 1_000, reportedOutput: null);
        WritePricedTurn(sqlite, memory, "sess-reported-output", tokens: [], reportedInput: null, reportedOutput: 100);
        WritePricedTurn(
            sqlite,
            memory,
            "sess-reconciled",
            tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 100),
                new CollectedTokenCount(TokenClass.Output, 10),
            ],
            reportedInput: 100,
            reportedOutput: null);
        WritePricedTurn(
            sqlite,
            memory,
            "sess-remainder",
            tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 100),
                new CollectedTokenCount(TokenClass.Output, 10),
            ],
            reportedInput: 300,
            reportedOutput: null);
        WritePricedTurn(
            sqlite,
            memory,
            "sess-reported-output-fills",
            tokens: [new CollectedTokenCount(TokenClass.Input, 100)],
            reportedInput: 100,
            reportedOutput: 40);
        WritePricedTurn(
            sqlite,
            memory,
            "sess-output-only",
            tokens: [new CollectedTokenCount(TokenClass.Output, 10)],
            reportedInput: null,
            reportedOutput: null);
        WritePricedTurn(
            sqlite,
            memory,
            "sess-input-only",
            tokens: [new CollectedTokenCount(TokenClass.Input, 100)],
            reportedInput: null,
            reportedOutput: null);
        WritePricedTurn(
            sqlite,
            memory,
            "sess-reasoning-only",
            tokens: [new CollectedTokenCount(TokenClass.Reasoning, 20)],
            reportedInput: null,
            reportedOutput: null);

        AssertPricedAlike(sqlite, memory, pricing, "sess-absent", CostStatus.NoRate, null);
        AssertPricedAlike(sqlite, memory, pricing, "sess-zero", CostStatus.Priced, 0d);
        AssertPricedAlike(sqlite, memory, pricing, "sess-reported-both", CostStatus.Partial, 100 * outputRate);
        AssertPricedAlike(sqlite, memory, pricing, "sess-reported-input", CostStatus.NoRate, null);
        AssertPricedAlike(sqlite, memory, pricing, "sess-reported-output", CostStatus.Partial, 100 * outputRate);
        AssertPricedAlike(sqlite, memory, pricing, "sess-reconciled", CostStatus.Priced, (100 * inputRate) + (10 * outputRate));
        AssertPricedAlike(sqlite, memory, pricing, "sess-remainder", CostStatus.Partial, (100 * inputRate) + (10 * outputRate));
        AssertPricedAlike(sqlite, memory, pricing, "sess-reported-output-fills", CostStatus.Priced, (100 * inputRate) + (40 * outputRate));
        AssertPricedAlike(sqlite, memory, pricing, "sess-output-only", CostStatus.Partial, 10 * outputRate);
        AssertPricedAlike(sqlite, memory, pricing, "sess-input-only", CostStatus.Partial, 100 * inputRate);
        AssertPricedAlike(sqlite, memory, pricing, "sess-reasoning-only", CostStatus.NoRate, null);
    }

    private static void WritePricedTurn(
        SqliteRelationalStore sqlite,
        InMemoryRelationalStore memory,
        string sessionKey,
        IReadOnlyList<CollectedTokenCount> tokens,
        long? reportedInput,
        long? reportedOutput)
    {
        CollectedRecord record = new(
            RecordId: sessionKey + "-turn",
            HarnessId: "codex-cli",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "codex.turn",
            StartedAt: new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-6",
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: tokens,
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: reportedInput,
            ReportedOutputTokens: reportedOutput);
        CollectedBatch batch = new(
            Source: new CollectedSource("codex-cli", SourceKind.File, sessionKey),
            Sessions: [new CollectedSession(sessionKey, "codex-cli", sessionKey, null, null, [])],
            Records: [record],
            Problems: []);
        sqlite.WriteBatch(batch);
        memory.WriteBatch(batch);
    }

    private static void WriteAttributionTurn(
        SqliteRelationalStore sqlite,
        InMemoryRelationalStore memory,
        string spanId,
        string sessionKey,
        string harnessId)
    {
        CollectedRecord record = new(
            RecordId: spanId,
            HarnessId: harnessId,
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: sessionKey,
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [new CollectedTokenCount(TokenClass.Input, 100), new CollectedTokenCount(TokenClass.Output, 10)],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: 100,
            ReportedOutputTokens: 10);
        CollectedBatch batch = new(
            Source: new CollectedSource(harnessId, SourceKind.Otlp, "otlp:" + harnessId),
            Sessions: [new CollectedSession(sessionKey, harnessId, sessionKey, null, null, [])],
            Records: [record],
            Problems: []);
        sqlite.WriteBatch(batch);
        memory.WriteBatch(batch);
    }

    /// <summary>
    /// Writes one four-part turn whose plain texts total 17 UTF-8 bytes (5, 4, 2, 6), so an
    /// 8-byte budget keeps the first two parts — the second crossing the budget — and cuts the
    /// last two.
    /// </summary>
    private static void WriteBudgetedContentTurn(SqliteRelationalStore sqlite, InMemoryRelationalStore memory)
    {
        CollectedRecord record = new(
            RecordId: "span-budget-1",
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "chat.turn",
            StartedAt: new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero),
            Status: RecordStatus.Ok,
            SessionKey: "sess-budget",
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [new CollectedTokenCount(TokenClass.Input, 100), new CollectedTokenCount(TokenClass.Output, 10)],
            ReportedCost: null,
            Parts:
            [
                BudgetPart(0, "aaaaa"),
                BudgetPart(1, "bbbb"),
                BudgetPart(2, "cc"),
                BudgetPart(3, "dddddd"),
            ],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: 100,
            ReportedOutputTokens: 10);
        CollectedBatch batch = new(
            Source: new CollectedSource("claude-code", SourceKind.Otlp, "otlp:claude-code"),
            Sessions: [new CollectedSession("sess-budget", "claude-code", "sess-budget", null, null, [])],
            Records: [record],
            Problems: []);
        sqlite.WriteBatch(batch);
        memory.WriteBatch(batch);
    }

    private static CollectedContentPart BudgetPart(int position, string text) => new(
        Position: position,
        Bucket: ContentBucket.UserPrompt,
        PlainText: text,
        Withheld: null,
        ReportedTokens: null,
        Server: null,
        Role: null,
        Resident: null,
        OriginalUtf8BytesWhenTruncated: null);

    private static void AssertPricedAlike(
        SqliteRelationalStore sqlite,
        InMemoryRelationalStore memory,
        IModelPricingLookup pricing,
        string sessionKey,
        CostStatus status,
        double? value)
    {
        SessionSummaryFigure memoryView = memory.SessionSummaryFigures(sessionKey, pricing)!;
        SessionSummaryFigure sqliteView = sqlite.SessionSummaryFigures(sessionKey, pricing)!;
        AssertPublishedCost(memoryView, status, value);
        AssertPublishedCost(sqliteView, status, value);
    }

    private static void AssertPublishedCost(SessionSummaryFigure view, CostStatus status, double? value)
    {
        Assert.Equal(status, view.PublishedCost.Status);
        if (value is null)
        {
            Assert.Null(view.PublishedCost.Value);
            return;
        }

        Assert.NotNull(view.PublishedCost.Value);
        Assert.Equal(value.Value, view.PublishedCost.Value.Value, precision: 8);
    }

    /// <summary>
    /// Verifies complete parity between <see cref="SqliteRelationalStore"/> and
    /// <see cref="InMemoryRelationalStore"/> across multi-harness batches, special characters,
    /// filtered and unfiltered queries, stored-fields inventory and per-table rows with parent-key
    /// diagnostics, and all <c>DD-13</c> / <c>OD-3</c> aggregate scenarios.
    /// </summary>
    [Fact]
    public void PopulatedStore_MatchesInMemoryRelationalStoreAcrossFilteredAndUnfilteredQueries()
    {
        string dbPath = Path.Combine(directory.Path, "populated", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        SeedPopulatedStores(sqlite, memory);
        sqlite.ClearRecordedSql();

        AssertQueryParity(
            sqlite,
            memory,
            sessionKeys: ["sess-proj", "sess-unproj", "sess-empty", "sess-unpriced", "sess-copilot", "sess-tokenless", "sess-zero-tokens", "sess-multi", "sess-missing"],
            harnessIds: [null, "claude-code", "copilot", "codex", "unknown-harness"],
            runIds: ["run-cc", "run-cp", "run-missing"],
            findingIds: ["find-1", "find-2", "find-missing"],
            spanIds: ["span-req-1", "span-turn-1", "span-req-2", "span-unproj-1", "span-tokenless-1", "span-multi-cx-1", "span-missing"]);

        AssertAllReadScriptsUseTabbedHexWithoutJson(sqlite.ExecutedSql);

        // Verify custom IModelPricingLookup and PricingTables produce identical results in both stores.
        ModelCosts customRates = new(
            InputCostPerToken: 0.00001,
            OutputCostPerToken: 0.00004,
            CacheWriteCostPerToken: 0.0000125,
            CacheReadCostPerToken: 0.000001,
            WebSearchCostPerRequest: 0.0,
            FastMultiplier: 1.0,
            CacheWriteCostIsExplicit: true);
        PricingTables customTables = new(
            new Dictionary<string, ModelCosts>(StringComparer.Ordinal) { ["custom-model-x"] = customRates },
            ["custom-model-x"],
            new Dictionary<string, ModelCosts>(StringComparer.Ordinal) { ["custom-model-x"] = customRates },
            new HashSet<string>(StringComparer.Ordinal) { "corp" });
        IModelPricingLookup customLookup = new OverridePricingLookup("claude-sonnet-4-5", customRates);

        Assert.Equivalent(
            memory.SessionListFigures("claude-code", customLookup),
            sqlite.SessionListFigures("claude-code", customLookup),
            strict: true);
        Assert.Equivalent(
            memory.SessionSummaryFigures("sess-proj", customLookup),
            sqlite.SessionSummaryFigures("sess-proj", customLookup),
            strict: true);
        Assert.Equivalent(
            memory.TurnFigures("sess-proj", 0, customLookup),
            sqlite.TurnFigures("sess-proj", 0, customLookup),
            strict: true);
        Assert.Equivalent(
            memory.SessionSummaryFigures("sess-unpriced", customTables),
            sqlite.SessionSummaryFigures("sess-unpriced", customTables),
            strict: true);
        Assert.Equivalent(
            memory.SessionToolFigures("sess-proj", customLookup),
            sqlite.SessionToolFigures("sess-proj", customLookup),
            strict: true);

        // Spot-check key DD-13 and OD-3 invariants observed on the populated corpus.
        SessionSummaryFigure? projAgg = sqlite.SessionSummaryFigures("sess-proj");
        Assert.NotNull(projAgg);
        Assert.Equal(2, projAgg.TurnCount);
        Assert.Equal(2, projAgg.RequestCount);
        Assert.NotNull(projAgg.ReportedCost);
        Assert.Equal(CostBasis.Harness, projAgg.ReportedCost.Basis);
        Assert.Equal(CostBasis.Published, projAgg.PublishedCost.Basis);
        Assert.NotNull(projAgg.CostBasisMismatch);
        Assert.True(projAgg.Coverage.SchemaMeasurable);
        Assert.True(projAgg.Coverage.ContextMeasurable);

        SessionSummaryFigure? unprojAgg = sqlite.SessionSummaryFigures("sess-unproj");
        Assert.NotNull(unprojAgg);
        Assert.NotNull(unprojAgg.ReportedCost);
        Assert.Equal(CostStatus.Partial, unprojAgg.ReportedCost.Status);
        Assert.Null(unprojAgg.ReportedCost.Value);
        Assert.Null(unprojAgg.DurationMs);
        Assert.Null(unprojAgg.ToolCalls);
        Assert.False(unprojAgg.HasToolDefinitions);
        Assert.Contains(unprojAgg.Problems, static problem => problem.Code == CanonPricing.CostCurrencyMismatchCode);

        SessionSummaryFigure? emptyAgg = sqlite.SessionSummaryFigures("sess-empty");
        Assert.NotNull(emptyAgg);
        Assert.Equal(0, emptyAgg.TurnCount);
        Assert.Equal(0, emptyAgg.RequestCount);
        Assert.Null(emptyAgg.TotalInput);
        Assert.Null(emptyAgg.TotalOutput);
        Assert.Null(emptyAgg.TotalCacheRead);
        Assert.Null(emptyAgg.TotalCacheCreation);
        Assert.Null(emptyAgg.DurationMs);
        Assert.Null(emptyAgg.ToolCalls);
        Assert.Null(emptyAgg.ReportedCost);
        Assert.Equal(CostBasis.Unknown, emptyAgg.PublishedCost.Basis);
        Assert.Equal(CostStatus.NoRate, emptyAgg.PublishedCost.Status);
        Assert.Null(emptyAgg.CacheHitRatio);
        Assert.Null(emptyAgg.CacheCreationCoverage);
    }

    /// <summary>
    /// <see cref="SqliteRelationalStore.SessionListFigures"/> is one content-free pass: no
    /// executed script touches a content-part, tool-call, tool-definition, agent-invocation, or
    /// skill table, and every figure equals the session's
    /// <see cref="SqliteRelationalStore.SessionSummaryFigures"/> figures on a token-less, a
    /// zero-token, a multi-harness, an empty, and a content-bearing session — with absent token
    /// totals staying distinct from zero.
    /// </summary>
    [Fact]
    public void SessionListFigures_AreContentFreeAndMatchThePerSessionSummaryFigures()
    {
        string dbPath = Path.Combine(directory.Path, "list-figures", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        sqlite.ClearRecordedSql();
        IReadOnlyList<SessionListFigure> figures = sqlite.SessionListFigures();
        string[] scripts = [.. sqlite.ExecutedSql];
        Assert.NotEmpty(scripts);
        Assert.All(scripts, static sql => Assert.Contains(".mode tabs", sql, StringComparison.Ordinal));
        // The deny-list names tables, not substrings: the shared projection read legitimately
        // selects session_context_turn.builtin_tool_definition_tokens, a token-count scalar the
        // list read uses, and a bare "tool_definition" substring match would flag that scalar as
        // content.
        string[] contentTables =
        [
            "content_part",
            "tool_call",
            "tool_definition",
            "agent_invocation",
            "record_skill_invocation"
        ];
        Assert.All(scripts, sql =>
        {
            foreach (string table in contentTables)
            {
                Assert.DoesNotContain($"FROM {table}", sql, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain($"JOIN {table}", sql, StringComparison.OrdinalIgnoreCase);
            }
        });

        Assert.Equivalent(memory.SessionListFigures(), figures, strict: true);

        foreach (SessionListFigure figure in figures)
        {
            SessionSummaryFigure? summary = sqlite.SessionSummaryFigures(figure.SessionKey);
            Assert.NotNull(summary);
            Assert.Equal(summary.SessionKey, figure.SessionKey);
            Assert.Equal(summary.HarnessId, figure.HarnessId);
            Assert.Equal(summary.TurnCount, figure.TurnCount);
            Assert.Equal(summary.RequestCount, figure.RequestCount);
            Assert.Equal(summary.TotalInput, figure.TotalInput);
            Assert.Equal(summary.TotalOutput, figure.TotalOutput);
            Assert.Equal(summary.Models, figure.Models);
            Assert.Equivalent(summary.ReportedCost, figure.ReportedCost, strict: true);
            Assert.Equivalent(summary.PublishedCost, figure.PublishedCost, strict: true);
            Assert.Equivalent(summary.CostBasisMismatch, figure.CostBasisMismatch, strict: true);
            Assert.Equal(summary.Problems.Count, figure.ProblemsCount);
        }

        // The absent-versus-zero trap, on the adapter: a turn with no token rows leaves the
        // totals absent, and a turn with zero-valued rows leaves them zero.
        SessionListFigure tokenless = Assert.Single(figures, static figure => figure.SessionKey == "sess-tokenless");
        Assert.Null(tokenless.TotalInput);
        Assert.Null(tokenless.TotalOutput);
        SessionListFigure zeroed = Assert.Single(figures, static figure => figure.SessionKey == "sess-zero-tokens");
        Assert.Equal(0, zeroed.TotalInput);
        Assert.Equal(0, zeroed.TotalOutput);

        // The multi-harness session keeps every attributed harness's turns under a harness
        // filter — the per-session read the sessions list replaced.
        IReadOnlyList<SessionListFigure> claudeFigures = sqlite.SessionListFigures("claude-code");
        SessionListFigure multi = Assert.Single(claudeFigures, static figure => figure.SessionKey == "sess-multi");
        Assert.Equal("claude-code", multi.HarnessId);
        Assert.Equal("multi-repo", multi.Repo);
        Assert.Equal("multi-branch", multi.Branch);
        Assert.Equal(2, multi.TurnCount);
        Assert.Equal(300, multi.TotalInput);
        Assert.Equal(["claude-sonnet-4-5", "gpt-5.1-codex"], multi.Models);
    }

    /// <summary>
    /// The tool figures read prices tool-call and tool-definition metadata and the stored schema
    /// projection only: every statement it issues references just those tables plus the session
    /// and first-model identity rows its unused-range pricing resolves, and never a content-text
    /// table, the timeline, the context projection, or the session's whole projection. Its figures
    /// match <see cref="InMemoryRelationalStore"/> on a projected, an unprojected, and an unknown
    /// session.
    /// </summary>
    [Fact]
    public void SessionToolFigures_TouchOnlyToolTablesTheStoredSchemaAndTheIdentityRows()
    {
        string dbPath = Path.Combine(directory.Path, "tool-figures", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        foreach (string sessionKey in (string[])["sess-proj", "sess-unproj", "sess-missing"])
        {
            sqlite.ClearRecordedSql();
            SessionToolFigureSet figures = sqlite.SessionToolFigures(sessionKey);
            AssertScriptsTouchOnlyTables(sqlite.ExecutedSql, SessionToolFigureTables);
            Assert.Equivalent(memory.SessionToolFigures(sessionKey), figures, strict: true);
        }
    }

    /// <summary>
    /// The skill figures read scopes the stored skill invocations by the session's spans and
    /// touches the skill table alone, matching <see cref="InMemoryRelationalStore"/> row for row.
    /// </summary>
    [Fact]
    public void SessionSkillFigures_TouchOnlyTheSkillTable()
    {
        string dbPath = Path.Combine(directory.Path, "skill-figures", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        foreach (string sessionKey in (string[])["sess-proj", "sess-missing"])
        {
            sqlite.ClearRecordedSql();
            IReadOnlyList<StoredSkillInvocation> rows = sqlite.SessionSkillFigures(sessionKey);
            AssertScriptsTouchOnlyTables(sqlite.ExecutedSql, SessionSkillFigureTables);
            Assert.Equivalent(memory.SessionSkillFigures(sessionKey), rows, strict: true);
        }
    }

    /// <summary>
    /// The KW skill loads read returns exactly one session's KW skill loads, in call order, with
    /// the MCP split, the reply's head and the record's start time; another server's
    /// <c>load_skill</c> is not a load (D17) and is not returned. It touches only the tool-call
    /// tables, the record header and its session link, and it matches
    /// <see cref="InMemoryRelationalStore"/> on two sessions and an unknown one, page for page.
    /// </summary>
    [Fact]
    public void SessionKwSkillLoadsReadsOnlyThatSessionsLoads()
    {
        string dbPath = Path.Combine(directory.Path, "kw-skill-loads", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        WriteKwLoadSession(sqlite, memory, "sess-kw-a",
        [
            KwLoadRecord("span-kw-a-1", "sess-kw-a", 0, new CollectedToolCall("call-a-1", "load_skill", "kw-docs", "load_skill", null, KwLoadReply("skill: csharp-dev\n\nGuidance."), true, null, null)),
            KwLoadRecord("span-kw-a-2", "sess-kw-a", 1, new CollectedToolCall("call-a-2", "mcp__kw_docs__load_skill", null, null, null, KwLoadReply("skill: dal-dev\n\nGuidance."), true, null, null)),
            KwLoadRecord("span-kw-a-3", "sess-kw-a", 2, new CollectedToolCall("call-a-3", "read_file", null, null, null, KwLoadReply("skill: notes.md\n\nGuidance."), true, null, null)),
            KwLoadRecord("span-kw-a-4", "sess-kw-a", 3, new CollectedToolCall("call-a-4", "load_skill", "other-server", "load_skill", null, KwLoadReply("skill: sql-dev\n\nGuidance."), true, null, null)),
        ]);
        WriteKwLoadSession(sqlite, memory, "sess-kw-b",
        [
            KwLoadRecord("span-kw-b-1", "sess-kw-b", 0, new CollectedToolCall("call-b-1", "load_skill", "kw-docs", "load_skill", null, KwLoadReply("skill: tauri-dev\n\nGuidance."), true, null, null)),
        ]);

        foreach (string sessionKey in (string[])["sess-kw-a", "sess-kw-b", "sess-missing"])
        {
            sqlite.ClearRecordedSql();
            SessionKwSkillLoadPageResult rows = sqlite.SessionKwSkillLoads(sessionKey, 1, 25);
            AssertScriptsTouchOnlyTables(sqlite.ExecutedSql, SessionKwSkillLoadTables);
            Assert.Equivalent(memory.SessionKwSkillLoads(sessionKey, 1, 25), rows, strict: true);
        }

        SessionKwSkillLoadPageResult sessionA = sqlite.SessionKwSkillLoads("sess-kw-a", 1, 25);
        string[] expectedSpans = ["span-kw-a-1", "span-kw-a-2"];
        Assert.Equal(expectedSpans, sessionA.Items.Select(static row => row.SpanId).ToArray());
        Assert.Equal(expectedSpans.Length, sessionA.Total);
        Assert.DoesNotContain(sessionA.Items, static row => row.ToolName == "read_file");
        Assert.Equal("skill: csharp-dev\n\nGuidance.", sessionA.Items[0].ReplyHead);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero), sessionA.Items[0].StartedAtUtc);
        Assert.Single(sqlite.SessionKwSkillLoads("sess-kw-b", 1, 25).Items);

        // The second page continues the call order in both stores, so the offset answers from SQL.
        SessionKwSkillLoadPageResult secondPage = sqlite.SessionKwSkillLoads("sess-kw-a", 2, 1);
        Assert.Equivalent(memory.SessionKwSkillLoads("sess-kw-a", 2, 1), secondPage, strict: true);
        Assert.Equal(["span-kw-a-2"], secondPage.Items.Select(static row => row.SpanId).ToArray());
        Assert.Equal(expectedSpans.Length, secondPage.Total);
    }

    /// <summary>
    /// A load's reply is read only to its first 1024 characters: the SQLite reader and the
    /// in-memory fake return the same head, and a shorter reply comes back whole.
    /// </summary>
    [Fact]
    public void SessionKwSkillLoadsReturnsAtMostTheReplysFirst1024Characters()
    {
        string dbPath = Path.Combine(directory.Path, "kw-skill-reply-head", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        string longReply = "skill: csharp-dev\n\n" + new string('x', 2000);
        WriteKwLoadSession(sqlite, memory, "sess-kw-long",
        [
            KwLoadRecord("span-kw-long-1", "sess-kw-long", 0, new CollectedToolCall("call-long-1", "load_skill", "kw-docs", "load_skill", null, KwLoadReply(longReply), true, null, null)),
            KwLoadRecord("span-kw-long-2", "sess-kw-long", 1, new CollectedToolCall("call-long-2", "load_skill", "kw-docs", "load_skill", null, KwLoadReply("skill: dal-dev"), true, null, null)),
        ]);

        SessionKwSkillLoadPageResult rows = sqlite.SessionKwSkillLoads("sess-kw-long", 1, 25);

        Assert.Equal(longReply[..1024], rows.Items[0].ReplyHead);
        Assert.Equal(1024, rows.Items[0].ReplyHead!.Length);
        Assert.Equal("skill: dal-dev", rows.Items[1].ReplyHead);
        Assert.Equal(2, rows.Total);
        Assert.Equivalent(memory.SessionKwSkillLoads("sess-kw-long", 1, 25), rows, strict: true);
    }

    private static readonly string[] SessionKwSkillLoadTables =
    [
        "tool_call",
        "tool_call_mcp",
        "tool_call_result_text",
        "record",
        "record_session_link",
    ];

    /// <summary>
    /// Reply text for the reader test: a plain-text reply, since the store refuses JSON object text
    /// on write (<c>STORE_JSON_TEXT_REFUSED</c>). The call's input is never seeded.
    /// </summary>
    private static CollectedToolContent KwLoadReply(string reply) =>
        new(reply, System.Text.Encoding.UTF8.GetByteCount(reply), reply.Length, null, null);

    private static CollectedRecord KwLoadRecord(string spanId, string sessionKey, int minuteOffset, CollectedToolCall tool) => new(
        RecordId: spanId,
        HarnessId: "claude-code",
        Op: RecordOp.LlmInvoke,
        Kind: RecordKind.Client,
        Name: "tool.call",
        StartedAt: new DateTimeOffset(2026, 11, 19, 12, minuteOffset, 0, TimeSpan.Zero),
        Status: RecordStatus.Ok,
        SessionKey: sessionKey,
        ParentRecordId: null,
        TraceId: null,
        Model: "claude-sonnet-4-6",
        EndedAt: null,
        ReportedDurationMs: null,
        Tokens: [],
        ReportedCost: null,
        Parts: [],
        Tool: tool,
        Agent: null,
        SkillsInvoked: [],
        AgentInstanceId: null,
        ToolDefinitions: [],
        Signals: new CollectedRecordSignals(null, null, null, null),
        ReportedInputTokens: null,
        ReportedOutputTokens: null);

    private static void WriteKwLoadSession(
        SqliteRelationalStore sqlite,
        InMemoryRelationalStore memory,
        string sessionKey,
        IReadOnlyList<CollectedRecord> records)
    {
        CollectedBatch batch = new(
            Source: new CollectedSource("claude-code", SourceKind.File, sessionKey),
            Sessions: [new CollectedSession(sessionKey, "claude-code", sessionKey, null, null, [])],
            Records: records,
            Problems: []);
        sqlite.WriteBatch(batch);
        memory.WriteBatch(batch);
    }

    /// <summary>
    /// The timeline records read scopes to the record-header columns the timeline tree's nodes
    /// carry: every statement it issues references just the record header, its session link,
    /// parent link, model, reported duration, reported cost and declared window — and never a
    /// content-text, token, tool, skill, or trace table. Its rows match
    /// <see cref="InMemoryRelationalStore"/> on a projected, an unprojected, and an unknown
    /// session.
    /// </summary>
    [Fact]
    public void SessionTimelineRecords_TouchOnlyTheTimelineHeaderTables()
    {
        string dbPath = Path.Combine(directory.Path, "timeline-records", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        foreach (string sessionKey in (string[])["sess-proj", "sess-unproj", "sess-missing"])
        {
            sqlite.ClearRecordedSql();
            IReadOnlyList<CollectedRecord> records = sqlite.SessionTimelineRecords(sessionKey);
            AssertScriptsTouchOnlyTables(sqlite.ExecutedSql, SessionTimelineRecordTables);
            Assert.Equivalent(memory.SessionTimelineRecords(sessionKey), records, strict: true);
        }
    }

    /// <summary>
    /// The stored-fields inventory reads no row values: its script answers every catalog table's
    /// count with <c>COUNT(*)</c> and touches only key columns — never the content-text column a
    /// part carries — and its entries match <see cref="InMemoryRelationalStore"/> (decision D15).
    /// </summary>
    [Fact]
    public void StoredFieldsInventory_CountsWithoutReadingRowValues()
    {
        string dbPath = Path.Combine(directory.Path, "stored-fields-inventory", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        sqlite.ClearRecordedSql();
        StoredFieldsInventoryView inventory = sqlite.StoredFieldsInventory("sess-proj");

        Assert.NotEmpty(sqlite.ExecutedSql);
        Assert.All(sqlite.ExecutedSql, static sql =>
        {
            Assert.Contains("COUNT(*)", sql, StringComparison.Ordinal);
            // The one column a content part's text lives in: the inventory's key-column selects
            // never read it. The match anchors on the CAST form so the table name
            // content_part_text cannot satisfy it.
            Assert.DoesNotContain("CAST(part_text", sql, StringComparison.Ordinal);
        });
        Assert.Equivalent(memory.StoredFieldsInventory("sess-proj"), inventory, strict: true);
    }

    /// <summary>
    /// A table's rows are limited in SQL: the page statement carries the <c>LIMIT</c> the caller
    /// asked for, and the page matches <see cref="InMemoryRelationalStore">the in-memory
    /// store's</see> page for the same table (decision D15).
    /// </summary>
    [Fact]
    public void StoredFieldsRows_LimitsThePageInSql()
    {
        string dbPath = Path.Combine(directory.Path, "stored-fields-rows", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        sqlite.ClearRecordedSql();
        StoredTableRows? page = sqlite.StoredFieldsRows("sess-proj", "record", 2, 25);

        Assert.NotNull(page);
        Assert.NotEmpty(sqlite.ExecutedSql);
        Assert.All(sqlite.ExecutedSql, static sql => Assert.Contains("LIMIT 25 OFFSET 25", sql, StringComparison.Ordinal));
        Assert.Equivalent(memory.StoredFieldsRows("sess-proj", "record", 2, 25), page, strict: true);
        Assert.Equal(25, page.Limit);
    }

    /// <summary>A table name outside the catalog reads as absent — the allowlist is the catalog.</summary>
    [Fact]
    public void StoredFieldsRows_ReturnsNullForATableOutsideTheCatalog()
    {
        string dbPath = Path.Combine(directory.Path, "stored-fields-unknown-table", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        sqlite.ClearRecordedSql();

        Assert.Null(sqlite.StoredFieldsRows("sess-proj", "sqlite_master", 1, 25));
        Assert.Empty(sqlite.ExecutedSql);
    }

    /// <summary>
    /// The SQL root rule keeps today's tree semantics: a session is a root unless its parent is a
    /// projected session in the same harness-filtered set and attaching it would not form a
    /// cycle. An orphaned child (its parent has no projection), a child whose parent sits
    /// outside the harness filter, and a cycle whose members land on different pages all keep
    /// their root membership, page by page, on both stores.
    /// </summary>
    [Fact]
    public void SessionHeaderPageMatchesTheTreeRule()
    {
        string dbPath = Path.Combine(directory.Path, "header-page-tree", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        WriteAttributionTurn(sqlite, memory, "span-orphan", "sess-orphan", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-pc", "sess-pc", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-px", "sess-px", "codex");
        WriteAttributionTurn(sqlite, memory, "span-cycle-a", "sess-cycle-a", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-cycle-b", "sess-cycle-b", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-cycle-c", "sess-cycle-c", "claude-code");

        WriteProjection(sqlite, memory, "sess-orphan", "orphan child", parentSessionId: "sess-ghost", isSubagent: true, minute: 10);
        WriteProjection(sqlite, memory, "sess-pc", "child of a codex parent", parentSessionId: "sess-px", isSubagent: true, minute: 20);
        WriteProjection(sqlite, memory, "sess-px", "codex root", parentSessionId: null, isSubagent: false, minute: 30);
        WriteProjection(sqlite, memory, "sess-cycle-a", "cycle a", parentSessionId: "sess-cycle-b", isSubagent: false, minute: 40);
        WriteProjection(sqlite, memory, "sess-cycle-b", "cycle b", parentSessionId: "sess-cycle-c", isSubagent: false, minute: 50);
        WriteProjection(sqlite, memory, "sess-cycle-c", "cycle c", parentSessionId: "sess-cycle-a", isSubagent: false, minute: 60);

        // Unfiltered, limit 2: the cycle members land on different pages and every one stays a
        // root; the child of the codex parent folds under it and leaves the root set.
        AssertHeaderPage(sqlite, memory, null, null, 1, 2, ["sess-cycle-c", "sess-cycle-b"], 5);
        AssertHeaderPage(sqlite, memory, null, null, 2, 2, ["sess-cycle-a", "sess-px"], 5);
        AssertHeaderPage(sqlite, memory, null, null, 3, 2, ["sess-orphan"], 5);
        AssertHeaderPage(sqlite, memory, null, null, 4, 2, [], 5);

        // The harness filter scopes the root rule: the codex parent leaves the set, so its
        // claude-code child becomes a root, while the in-filter cycle stays a cycle.
        AssertHeaderPage(sqlite, memory, "claude-code", null, 1, 2, ["sess-cycle-c", "sess-cycle-b"], 5);
        AssertHeaderPage(sqlite, memory, "claude-code", null, 2, 2, ["sess-cycle-a", "sess-pc"], 5);
        AssertHeaderPage(sqlite, memory, "claude-code", null, 3, 2, ["sess-orphan"], 5);
        AssertHeaderPage(sqlite, memory, "codex", null, 1, 10, ["sess-px"], 1);

        // The subagent preview and the child page answer the attachable children of a parent —
        // never a cycle member that would loop back — and skip parents with no children.
        IReadOnlyList<string> parents = ["sess-px", "sess-cycle-a", "sess-orphan", "sess-missing"];
        Assert.Equivalent(memory.SessionChildPreview(parents, 25), sqlite.SessionChildPreview(parents, 25), strict: true);
        SessionChildPreviewEntry preview = Assert.Single(sqlite.SessionChildPreview(parents, 25));
        Assert.Equal("sess-px", preview.ParentSessionId);
        Assert.Equal(1, preview.TotalChildren);
        Assert.Equal(["sess-pc"], preview.ChildSessionIds);

        SessionHeaderPageResult children = sqlite.SessionChildHeaderPage("sess-px", 1, 25);
        Assert.Equal(["sess-pc"], [.. children.Items.Select(static header => header.SessionId)]);
        Assert.Equal(1, children.Total);
        Assert.Equivalent(memory.SessionChildHeaderPage("sess-px", 1, 25), children, strict: true);
        Assert.Empty(sqlite.SessionChildHeaderPage("sess-cycle-a", 1, 25).Items);
        Assert.Equal(0, sqlite.SessionChildHeaderPage("sess-cycle-a", 1, 25).Total);
    }

    /// <summary>
    /// The header-page search matches its text literally on both stores: <c>%</c>, <c>_</c>, a
    /// backslash, and a quote in the search text are data, never wildcard or escape syntax, and
    /// the search covers the label and the session id.
    /// </summary>
    [Fact]
    public void SessionHeaderPageSearchMatchesLiteralText()
    {
        string dbPath = Path.Combine(directory.Path, "header-page-search", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        WriteAttributionTurn(sqlite, memory, "span-pct", "sess-pct", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-trap", "sess-trap", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-score", "sess-score", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-back", "sess-back", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-quote", "sess-quote", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-plain", "sess-plain", "claude-code");

        WriteProjection(sqlite, memory, "sess-pct", "rate 100% done", null, false, 10);
        WriteProjection(sqlite, memory, "sess-trap", "underXscore trap", null, false, 20);
        WriteProjection(sqlite, memory, "sess-score", "under_score fix", null, false, 30);
        WriteProjection(sqlite, memory, "sess-back", "log C:\\temp path", null, false, 40);
        WriteProjection(sqlite, memory, "sess-quote", "it's quoted", null, false, 50);
        WriteProjection(sqlite, memory, "sess-plain", "ordinary work", null, false, 60);

        AssertHeaderPage(sqlite, memory, null, "100% d", 1, 10, ["sess-pct"], 1);
        // The unescaped wildcard reading would also admit sess-trap; the literal reading does not.
        AssertHeaderPage(sqlite, memory, null, "under_score", 1, 10, ["sess-score"], 1);
        AssertHeaderPage(sqlite, memory, null, "C:\\te", 1, 10, ["sess-back"], 1);
        AssertHeaderPage(sqlite, memory, null, "t's qu", 1, 10, ["sess-quote"], 1);
        AssertHeaderPage(sqlite, memory, null, "sess-pl", 1, 10, ["sess-plain"], 1);
        AssertHeaderPage(sqlite, memory, null, "nothing matches", 1, 10, [], 0);
    }

    /// <summary>
    /// The sessions page path issues only scoped statements over the recording seam. The header
    /// page query is the one statement that carries no keys — it is the page query. Every other
    /// statement is bound to the whole key list of its pass: the subagent preview to the page's
    /// roots, the keyed header fetch to the preview's children, and the figures pass to every key
    /// on the page. The preview is exactly one statement for every root on the page, never one
    /// query per root, and the figures pass issues as many statements for five keys as for one.
    /// </summary>
    [Fact]
    public void TheSessionsPageIssuesNoUnkeyedStatement()
    {
        string dbPath = Path.Combine(directory.Path, "header-page-keyed", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        WriteAttributionTurn(sqlite, memory, "span-r1", "sess-r1", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-r2", "sess-r2", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-r3", "sess-r3", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-r2-a", "sess-r2-a", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-r2-b", "sess-r2-b", "claude-code");

        WriteProjection(sqlite, memory, "sess-r1", "root one", null, false, 10);
        WriteProjection(sqlite, memory, "sess-r2", "root two", null, false, 20);
        WriteProjection(sqlite, memory, "sess-r3", "root three", null, false, 30);
        WriteProjection(sqlite, memory, "sess-r2-a", "child a", "sess-r2", true, 21);
        WriteProjection(sqlite, memory, "sess-r2-b", "child b", "sess-r2", true, 22);

        (SessionHeaderPageResult Page, string[] Scripts) headerRead = Recorded(sqlite, () => sqlite.SessionHeaderPage(null, null, 1, 100));
        string[] rootIds = [.. headerRead.Page.Items.Select(static header => header.SessionId)];
        (IReadOnlyList<SessionChildPreviewEntry> Preview, string[] Scripts) previewRead = Recorded(sqlite, () => sqlite.SessionChildPreview(rootIds, 25));
        string[] childIds = [.. previewRead.Preview.SelectMany(static entry => entry.ChildSessionIds)];
        (IReadOnlyList<StoredSessionHeader> Headers, string[] Scripts) childHeaderRead = Recorded(sqlite, () => sqlite.SessionHeadersByKeys(childIds));
        string[] pageKeys = [.. rootIds.Concat(childIds)];
        (IReadOnlyList<SessionListFigure> Figures, string[] Scripts) figureRead = Recorded(sqlite, () => sqlite.SessionListFigures(pageKeys));

        Assert.Equal(["sess-r3", "sess-r2", "sess-r1"], rootIds.ToList());
        Assert.Equal(["sess-r2-a", "sess-r2-b"], childIds.ToList());
        Assert.Equal(2, childHeaderRead.Headers.Count);
        Assert.Equal(5, figureRead.Figures.Count);

        // The page query is one statement: the root rule and its window, with no key to carry.
        string pageScript = Assert.Single(headerRead.Scripts);
        Assert.Contains("WITH RECURSIVE scope", pageScript, StringComparison.Ordinal);

        // One window-function statement covers every root's preview — no per-root child query —
        // and it names the roots only: the children are found by the join, not asked for.
        string previewScript = Assert.Single(previewRead.Scripts);
        Assert.Contains("row_number() OVER", previewScript, StringComparison.Ordinal);
        Assert.All(rootIds, id => Assert.Contains(KeyLiteral(id), previewScript, StringComparison.Ordinal));
        Assert.All(childIds, id => Assert.DoesNotContain(KeyLiteral(id), previewScript, StringComparison.Ordinal));

        // The keyed header fetch names the preview's children and nothing else.
        string childHeaderScript = Assert.Single(childHeaderRead.Scripts);
        Assert.All(childIds, id => Assert.Contains(KeyLiteral(id), childHeaderScript, StringComparison.Ordinal));
        Assert.All(rootIds, id => Assert.DoesNotContain(KeyLiteral(id), childHeaderScript, StringComparison.Ordinal));

        // Every statement of the figures pass is bound to the whole page, and the pass issues the
        // same number of statements however many keys it prices.
        Assert.NotEmpty(figureRead.Scripts);
        Assert.All(
            figureRead.Scripts,
            sql => Assert.All(pageKeys, id => Assert.Contains(KeyLiteral(id), sql, StringComparison.Ordinal)));
        (IReadOnlyList<SessionListFigure> _, string[] SingleKeyScripts) singleKeyRead = Recorded(sqlite, () => sqlite.SessionListFigures(["sess-r1"]));
        Assert.Equal(singleKeyRead.SingleKeyScripts.Length, figureRead.Scripts.Length);

        string[] scripts = [.. headerRead.Scripts, .. previewRead.Scripts, .. childHeaderRead.Scripts, .. figureRead.Scripts];
        Assert.NotEmpty(scripts);
        Assert.All(scripts, static sql => Assert.Contains(".mode tabs", sql, StringComparison.Ordinal));
    }

    /// <summary>
    /// The child reads follow the root rule under a cycle: a session whose parent sits on a
    /// cycle is not on that cycle itself, so it attaches to its parent while every cycle member
    /// stays a root with no subagents of its own.
    /// </summary>
    [Fact]
    public void AChildOfACycleMemberAttachesWhileTheCycleMembersStayRoots()
    {
        string dbPath = Path.Combine(directory.Path, "header-page-cycle-child", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        WriteAttributionTurn(sqlite, memory, "span-cycle-x", "sess-cycle-x", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-cycle-y", "sess-cycle-y", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-under-x", "sess-under-x", "claude-code");
        WriteAttributionTurn(sqlite, memory, "span-under-under", "sess-under-under", "claude-code");

        WriteProjection(sqlite, memory, "sess-cycle-x", "cycle x", "sess-cycle-y", false, 10);
        WriteProjection(sqlite, memory, "sess-cycle-y", "cycle y", "sess-cycle-x", false, 20);
        WriteProjection(sqlite, memory, "sess-under-x", "child of cycle x", "sess-cycle-x", true, 30);
        WriteProjection(sqlite, memory, "sess-under-under", "grandchild", "sess-under-x", true, 40);

        // Both cycle members are roots; the child under the cycle, and the grandchild under it,
        // attach. The second page is past the end and still reports both roots.
        AssertHeaderPage(sqlite, memory, null, null, 1, 10, ["sess-cycle-y", "sess-cycle-x"], 2);
        AssertHeaderPage(sqlite, memory, null, null, 2, 10, [], 2);

        // The preview answers in root-page order — the newest parent first — with one child under
        // the cycle member and one under that child.
        string[] parents = ["sess-cycle-x", "sess-cycle-y", "sess-under-x"];
        IReadOnlyList<SessionChildPreviewEntry> previews = sqlite.SessionChildPreview(parents, 25);
        Assert.Equivalent(memory.SessionChildPreview(parents, 25), previews, strict: true);
        Assert.Equal(["sess-under-x", "sess-cycle-x"], previews.Select(static entry => entry.ParentSessionId).ToList());
        Assert.All(previews, static entry => Assert.Equal(1, entry.TotalChildren));
        Assert.Equal(["sess-under-under"], previews[0].ChildSessionIds);
        Assert.Equal(["sess-under-x"], previews[1].ChildSessionIds);

        SessionHeaderPageResult children = sqlite.SessionChildHeaderPage("sess-cycle-x", 1, 25);
        Assert.Equivalent(memory.SessionChildHeaderPage("sess-cycle-x", 1, 25), children, strict: true);
        Assert.Equal(1, children.Total);
        Assert.Equal("sess-under-x", Assert.Single(children.Items).SessionId);
    }

    /// <summary>
    /// The paged header reads refuse a page or a limit below 1 on both stores, instead of
    /// answering a window the caller did not ask for.
    /// </summary>
    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 0)]
    [InlineData(-1, 25)]
    public void PagedHeaderReadsRefuseAPageOrLimitBelowOne(int page, int limit)
    {
        string dbPath = Path.Combine(directory.Path, "header-page-arguments", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => sqlite.SessionHeaderPage(null, null, page, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => memory.SessionHeaderPage(null, null, page, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => sqlite.SessionChildHeaderPage("sess-missing", page, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => memory.SessionChildHeaderPage("sess-missing", page, limit));
    }

    /// <summary>Runs one read over the recording seam and answers its result with the scripts it issued.</summary>
    private static (T Result, string[] Scripts) Recorded<T>(SeamSqliteRelationalStore sqlite, Func<T> read)
    {
        sqlite.ClearRecordedSql();
        return (read(), [.. sqlite.ExecutedSql]);
    }

    /// <summary>The injection-proof literal the data layer binds a text key as.</summary>
    private static string KeyLiteral(string key) => "CAST(X'" + ToHex(key) + "' AS TEXT)";

    /// <summary>
    /// The keyed figures read answers exactly the unkeyed sessions-list read for the same keys on
    /// the named parity seeds — token-less, multi-harness, empty, and content-bearing — so paging
    /// changes where the figures are read, never what they say.
    /// </summary>
    [Fact]
    public void KeyedSessionListFiguresMatchTheUnkeyedReadOnTheNamedSeeds()
    {
        string dbPath = Path.Combine(directory.Path, "keyed-figures", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        IReadOnlyList<SessionListFigure> unkeyed = sqlite.SessionListFigures();
        string[] keys = [.. unkeyed.Select(static figure => figure.SessionKey)];

        Assert.Equivalent(unkeyed, sqlite.SessionListFigures(keys), strict: true);
        Assert.Equivalent(memory.SessionListFigures(keys), sqlite.SessionListFigures(keys), strict: true);
        Assert.Empty(sqlite.SessionListFigures([]));

        // A subset answers that subset, so a page prices only its own keys.
        string[] pageKeys = [.. keys.Take(3)];
        HashSet<string> priced = [.. sqlite.SessionListFigures(pageKeys).Select(static figure => figure.SessionKey)];
        Assert.Equal(new HashSet<string>(pageKeys, StringComparer.Ordinal), priced);
    }

    /// <summary>
    /// The keyed report figures read prices each named session exactly as the per-session
    /// summary read prices it, and both stores agree on the same parity seeds — token-less,
    /// multi-harness, empty, and content-bearing — so batching changes where the report's
    /// figures are read, never what they say.
    /// </summary>
    [Fact]
    public void KeyedSessionReportFiguresMatchThePerSessionSummaryReadOnTheNamedSeeds()
    {
        string dbPath = Path.Combine(directory.Path, "keyed-report-figures", "canon.db");
        SeamSqliteRelationalStore sqlite = new(dbPath, new SqliteCircuitBreaker());
        InMemoryRelationalStore memory = new();
        SeedPopulatedStores(sqlite, memory);

        string[] keys = [.. sqlite.SessionListFigures().Select(static figure => figure.SessionKey)];

        IReadOnlyList<SessionReportFigure> sqliteFigures = sqlite.SessionReportFigures(keys);
        Assert.Equivalent(memory.SessionReportFigures(keys), sqliteFigures, strict: true);
        Assert.Empty(sqlite.SessionReportFigures([]));

        foreach (SessionReportFigure figure in sqliteFigures)
        {
            SessionSummaryFigure? summary = sqlite.SessionSummaryFigures(figure.SessionKey);
            Assert.NotNull(summary);
            Assert.Equal(summary.SessionKey, figure.SessionKey);
            Assert.Equal(summary.TotalInput, figure.TotalInput);
            Assert.Equal(summary.TotalCacheRead, figure.TotalCacheRead);
            Assert.Equivalent(summary.ToolsOffered, figure.ToolsOffered, strict: true);
            Assert.Equivalent(summary.ToolsInvoked, figure.ToolsInvoked, strict: true);
            // Structural comparison: the two reads build separate ByModel list instances the
            // records' own equality would compare by reference.
            Assert.Equivalent(summary.ReportedCost, figure.ReportedCost, strict: true);
            Assert.Equivalent(summary.PublishedCost, figure.PublishedCost, strict: true);
        }

        // A subset answers that subset, so a report prices only its own window.
        string[] windowKeys = [.. keys.Take(3)];
        HashSet<string> priced = [.. sqlite.SessionReportFigures(windowKeys).Select(static figure => figure.SessionKey)];
        Assert.Equal(new HashSet<string>(windowKeys, StringComparer.Ordinal), priced);
    }

    /// <summary>Writes one projection row with a controlled tree shape onto both stores.</summary>
    private static void WriteProjection(
        SqliteRelationalStore sqlite,
        InMemoryRelationalStore memory,
        string sessionKey,
        string label,
        string? parentSessionId,
        bool isSubagent,
        int minute)
    {
        StoredSessionProjection projection = new(
            SessionId: sessionKey,
            Label: label,
            IsSubagent: isSubagent,
            SpanCount: 1,
            StartedAtUtc: ProjectionMinute(minute),
            EndedAtUtc: ProjectionMinute(minute + 1),
            ParentSessionId: parentSessionId,
            AgentName: isSubagent ? "worker" : null,
            IncludedInParent: false,
            RequestSpanIds: [],
            Turns: []);
        sqlite.ReplaceSessionProjection(projection);
        memory.ReplaceSessionProjection(projection);
    }

    private static string ProjectionMinute(int minute) =>
        new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.Zero)
            .AddMinutes(minute)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Asserts both stores answer the same header page, ids in order, with the total.</summary>
    private static void AssertHeaderPage(
        SqliteRelationalStore sqlite,
        InMemoryRelationalStore memory,
        string? harnessId,
        string? search,
        int page,
        int limit,
        string[] expectedIds,
        int total)
    {
        SessionHeaderPageResult sqlitePage = sqlite.SessionHeaderPage(harnessId, search, page, limit);
        Assert.Equal(expectedIds, sqlitePage.Items.Select(static header => header.SessionId));
        Assert.Equal(total, sqlitePage.Total);
        Assert.Equivalent(memory.SessionHeaderPage(harnessId, search, page, limit), sqlitePage, strict: true);
    }

    /// <summary>
    /// Verifies <see cref="SqliteRelationalStore"/> reader parsing fails closed with
    /// <see cref="StoredDataException"/> when SQLite returns invalid hex, wrong column
    /// counts, unrecognized enum wire tokens, or non-numeric scalar columns.
    /// </summary>
    [Fact]
    public void ScriptedSqliteRelationalStore_FailsClosedOnCorruptTabOrHexOrEnumOutput()
    {
        string dbPath = Path.Combine(directory.Path, "scripted", "canon.db");
        ScriptedSqliteRelationalStore store = new(dbPath, new SqliteCircuitBreaker());

        // Truncated column count in Harnesses() (single column query returning extra tab column).
        store.EnqueueResponse("636C617564652D636F6465\textra");
        Assert.Throws<StoredDataException>(() => store.Harnesses());

        // Invalid hex encoding in Harnesses().
        store.EnqueueResponse("ZZZZ");
        Assert.Throws<StoredDataException>(() => store.Harnesses());

        // Unknown SourceKind wire token in Sources().
        string hexSrcId = ToHex("src-1");
        string hexHarness = ToHex("claude-code");
        string hexBadKind = ToHex("bogus_kind");
        string hexLocator = ToHex("/tmp/a.jsonl");
        store.EnqueueResponse($"{hexSrcId}\t{hexHarness}\t{hexBadKind}\t{hexLocator}");
        Assert.Throws<StoredDataException>(() => store.Sources());

        // Non-numeric CountRecords output.
        store.EnqueueResponse("not-an-integer");
        Assert.Throws<StoredDataException>(() => store.CountRecords());

        // Unknown TokenClass in TokenCounts().
        string hexSpan = ToHex("span-1");
        string hexBadToken = ToHex("unknown_bucket");
        store.EnqueueResponse($"{hexSpan}\t{hexBadToken}\t42");
        Assert.Throws<StoredDataException>(() => store.TokenCounts());
    }

    private static void AssertAllReadScriptsUseTabbedHexWithoutJson(IReadOnlyList<string> scripts)
    {
        foreach (string sql in scripts)
        {
            Assert.Contains(".headers off", sql, StringComparison.Ordinal);
            Assert.Contains(".mode tabs", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("json_extract", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("json_object", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("json_array", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("json_each", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The tables the tool figures read may touch: the tool-call and tool-definition metadata, the
    /// stored schema projection and its unmeasurable-reason child, and — because the unused-range
    /// pricing resolves the session's harness and first turn model — the session and model
    /// identity rows. The list is closed: a content-text table, the timeline, the context
    /// projection, the turn tables, or the session's whole projection would fail it.
    /// </summary>
    private static readonly string[] SessionToolFigureTables =
    [
        "session",
        "record",
        "record_session_link",
        "record_model",
        "tool_call",
        "tool_call_outcome",
        "tool_call_error",
        "tool_definition",
        "tool_definition_server",
        "session_schema",
        "session_schema_unmeasurable",
        "session_schema_ranked_tool",
        "session_schema_ranked_tool_server",
        "session_schema_server",
        "session_schema_unused",
    ];

    /// <summary>The tables the skill figures read may touch: the skill table and its session scoping.</summary>
    private static readonly string[] SessionSkillFigureTables =
    [
        "record_skill_invocation",
        "record_session_link",
    ];

    /// <summary>
    /// The tables the timeline records read may touch: the record header, its session and parent
    /// links, and the model, reported duration, reported cost and declared-window columns the
    /// hydrated tree node carries. The list is closed: a content-text, token, tool, skill, or
    /// trace table would fail it.
    /// </summary>
    private static readonly string[] SessionTimelineRecordTables =
    [
        "record",
        "record_session_link",
        "record_parent",
        "record_model",
        "record_reported_duration",
        "record_reported_cost",
        "record_declared_context_window",
    ];

    /// <summary>
    /// Asserts every table a <c>FROM</c> or <c>JOIN</c> names is in the allowed set, so a closed
    /// table claim is checked over the whole script and not just a deny-list of substrings.
    /// </summary>
    private static void AssertScriptsTouchOnlyTables(IReadOnlyList<string> scripts, IReadOnlyCollection<string> allowed)
    {
        Assert.NotEmpty(scripts);
        Assert.All(scripts, sql =>
        {
            Assert.Contains(".headers off", sql, StringComparison.Ordinal);
            Assert.Contains(".mode tabs", sql, StringComparison.Ordinal);
            foreach (Match match in Regex.Matches(sql, "(?:FROM|JOIN)\\s+([A-Za-z_]+)", RegexOptions.IgnoreCase))
            {
                Assert.Contains(match.Groups[1].Value, allowed);
            }
        });
    }

    private static void AssertQueryParity(
        IRelationalStoreReader sqliteReader,
        IRelationalStoreReader memoryReader,
        IReadOnlyList<string> sessionKeys,
        IReadOnlyList<string?> harnessIds,
        IReadOnlyList<string> runIds,
        IReadOnlyList<string> findingIds,
        IReadOnlyList<string> spanIds)
    {
        IProjectionStore sqliteProj = (IProjectionStore)sqliteReader;
        IProjectionStore memoryProj = (IProjectionStore)memoryReader;

        // The retained IRelationalStoreReader row queries + the sessions-list read model +
        // StoredFields.
        Assert.Equal(memoryReader.Harnesses(), sqliteReader.Harnesses());
        Assert.Equivalent(memoryReader.Sources(), sqliteReader.Sources(), strict: true);
        Assert.Equal(memoryReader.CountRecords(), sqliteReader.CountRecords());
        Assert.Equal(memoryReader.SessionAttributions(), sqliteReader.SessionAttributions());
        Assert.Equivalent(memoryReader.AllSessionRecordsForMaintenance(), sqliteReader.AllSessionRecordsForMaintenance(), strict: true);
        Assert.Equivalent(memoryReader.Sessions(), sqliteReader.Sessions(), strict: true);
        Assert.Equivalent(memoryReader.SessionModelUsages(), sqliteReader.SessionModelUsages(), strict: true);
        Assert.Equivalent(memoryReader.SessionListFigures(), sqliteReader.SessionListFigures(), strict: true);
        AssertHeaderReadParity(memoryReader, sqliteReader, sessionKeys, harnessIds);

        foreach (string? harnessId in harnessIds)
        {
            Assert.Equivalent(memoryReader.Sources(harnessId), sqliteReader.Sources(harnessId), strict: true);
            Assert.Equal(memoryReader.CountRecords(harnessId), sqliteReader.CountRecords(harnessId));
            Assert.Equivalent(memoryReader.Sessions(harnessId), sqliteReader.Sessions(harnessId), strict: true);
            Assert.Equivalent(memoryReader.SessionListFigures(harnessId), sqliteReader.SessionListFigures(harnessId), strict: true);

            Assert.Equivalent(memoryProj.ListSessionProjections(harnessId), sqliteProj.ListSessionProjections(harnessId), strict: true);
            Assert.Equivalent(memoryProj.ListRuns(harnessId), sqliteProj.ListRuns(harnessId), strict: true);

            if (harnessId is not null)
            {
                Assert.Equivalent(memoryProj.ListExecutionsByHarness(harnessId), sqliteProj.ListExecutionsByHarness(harnessId), strict: true);
                Assert.Equivalent(memoryProj.GetHarnessRollup(harnessId), sqliteProj.GetHarnessRollup(harnessId), strict: true);
            }
        }

        Assert.Equivalent(memoryReader.SourceRecordCounts(), sqliteReader.SourceRecordCounts(), strict: true);

        foreach (string spanId in spanIds)
        {
            Assert.Equivalent(memoryReader.RecordContentFree(spanId), sqliteReader.RecordContentFree(spanId), strict: true);
            AssertPairFilterParity(memoryReader, sqliteReader, spanId, null);
        }

        foreach (string sessionKey in sessionKeys)
        {
            Assert.Equivalent(memoryReader.GetSession(sessionKey), sqliteReader.GetSession(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.SessionRecordsForRebuild(sessionKey), sqliteReader.SessionRecordsForRebuild(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.SessionModelUsages(sessionKey), sqliteReader.SessionModelUsages(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.StoredFieldsInventory(sessionKey), sqliteReader.StoredFieldsInventory(sessionKey), strict: true);
            foreach (TableDefinition catalogTable in RelationalSchemaCatalog.Tables)
            {
                Assert.Equivalent(
                    memoryReader.StoredFieldsRows(sessionKey, catalogTable.Name, 1, 25),
                    sqliteReader.StoredFieldsRows(sessionKey, catalogTable.Name, 1, 25),
                    strict: true);
            }

            Assert.Equivalent(memoryReader.SessionSummaryFigures(sessionKey), sqliteReader.SessionSummaryFigures(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.SessionTurnFigures(sessionKey), sqliteReader.SessionTurnFigures(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.TurnFigures(sessionKey, 0), sqliteReader.TurnFigures(sessionKey, 0), strict: true);
            Assert.Equivalent(memoryReader.SessionRequestFigures(sessionKey, 1, 25), sqliteReader.SessionRequestFigures(sessionKey, 1, 25), strict: true);
            Assert.Equivalent(memoryReader.SessionRequestFigures(sessionKey, 2, 25), sqliteReader.SessionRequestFigures(sessionKey, 2, 25), strict: true);
            Assert.Equivalent(memoryReader.SessionReconciliationFigures(sessionKey, 1, 25), sqliteReader.SessionReconciliationFigures(sessionKey, 1, 25), strict: true);
            Assert.Equivalent(memoryReader.SessionReconciliationFigures(sessionKey, 2, 25), sqliteReader.SessionReconciliationFigures(sessionKey, 2, 25), strict: true);
            Assert.Equivalent(memoryReader.SessionProblems(sessionKey, 1, 25), sqliteReader.SessionProblems(sessionKey, 1, 25), strict: true);
            Assert.Equivalent(memoryReader.SessionToolFigures(sessionKey), sqliteReader.SessionToolFigures(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.SessionSkillFigures(sessionKey), sqliteReader.SessionSkillFigures(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.SessionTimelineRecords(sessionKey), sqliteReader.SessionTimelineRecords(sessionKey), strict: true);
            Assert.Equal(
                memoryReader.RequestPromptPreviews(sessionKey, ["span-req-1", "span-req-2"]),
                sqliteReader.RequestPromptPreviews(sessionKey, ["span-req-1", "span-req-2"]));
            Assert.Equivalent(memoryReader.TurnFigures(sessionKey, int.MaxValue), sqliteReader.TurnFigures(sessionKey, int.MaxValue), strict: true);
            Assert.Equivalent(memoryReader.TurnContentParts(sessionKey), sqliteReader.TurnContentParts(sessionKey), strict: true);
            Assert.Equivalent(memoryReader.TurnContentParts(sessionKey, turnIndex: 0), sqliteReader.TurnContentParts(sessionKey, turnIndex: 0), strict: true);
            Assert.Equivalent(
                memoryReader.TurnContentParts(sessionKey, turnIndex: 0, budget: 8),
                sqliteReader.TurnContentParts(sessionKey, turnIndex: 0, budget: 8),
                strict: true);
            Assert.Equivalent(
                memoryReader.TurnContentParts(sessionKey, turnIndex: 0, budget: 8, full: true),
                sqliteReader.TurnContentParts(sessionKey, turnIndex: 0, budget: 8, full: true),
                strict: true);

            AssertPairFilterParity(memoryReader, sqliteReader, null, sessionKey);
            foreach (string spanId in spanIds.Take(2))
            {
                AssertPairFilterParity(memoryReader, sqliteReader, spanId, sessionKey);
            }

            Assert.Equivalent(memoryProj.GetSessionProjection(sessionKey), sqliteProj.GetSessionProjection(sessionKey), strict: true);
            Assert.Equivalent(memoryProj.GetSessionTurns(sessionKey), sqliteProj.GetSessionTurns(sessionKey), strict: true);
            Assert.Equivalent(memoryProj.ListFindings(sessionId: sessionKey), sqliteProj.ListFindings(sessionId: sessionKey), strict: true);
        }

        AssertPairFilterParity(memoryReader, sqliteReader, null, null);

        foreach (string runId in runIds)
        {
            Assert.Equivalent(memoryProj.GetRun(runId), sqliteProj.GetRun(runId), strict: true);
            Assert.Equivalent(memoryProj.ListExecutionsForRun(runId), sqliteProj.ListExecutionsForRun(runId), strict: true);
            Assert.Equivalent(memoryProj.ListFindings(runId: runId), sqliteProj.ListFindings(runId: runId), strict: true);
            Assert.Equivalent(memoryProj.ListPredictions(runId: runId), sqliteProj.ListPredictions(runId: runId), strict: true);
        }

        Assert.Equivalent(memoryProj.ListSessionProjections(), sqliteProj.ListSessionProjections(), strict: true);
        Assert.Equivalent(memoryProj.ListRuns(), sqliteProj.ListRuns(), strict: true);
        Assert.Equivalent(memoryProj.ListHarnessRollups(), sqliteProj.ListHarnessRollups(), strict: true);
        Assert.Equivalent(memoryProj.ListFindings(), sqliteProj.ListFindings(), strict: true);
        Assert.Equivalent(memoryProj.ListPredictions(), sqliteProj.ListPredictions(), strict: true);

        foreach (string findingId in findingIds)
        {
            Assert.Equivalent(memoryProj.GetFinding(findingId), sqliteProj.GetFinding(findingId), strict: true);
            Assert.Equivalent(memoryProj.ListPredictions(findingId: findingId), sqliteProj.ListPredictions(findingId: findingId), strict: true);
        }

        Assert.Equivalent(memoryProj.GetExecution("exec-root"), sqliteProj.GetExecution("exec-root"), strict: true);
        Assert.Equivalent(memoryProj.GetExecution("exec-missing"), sqliteProj.GetExecution("exec-missing"), strict: true);
        Assert.Equivalent(memoryProj.GetPrediction("pred-1"), sqliteProj.GetPrediction("pred-1"), strict: true);
        Assert.Equivalent(memoryProj.GetPrediction("pred-missing"), sqliteProj.GetPrediction("pred-missing"), strict: true);
    }

    /// <summary>
    /// Parity for the paged header reads, each on both stores: the header page across pages,
    /// harness scopes and a search; the keyed header fetch; the child page; the subagent
    /// preview; and the keyed figures read.
    /// </summary>
    private static void AssertHeaderReadParity(
        IRelationalStoreReader memoryReader,
        IRelationalStoreReader sqliteReader,
        IReadOnlyList<string> sessionKeys,
        IReadOnlyList<string?> harnessIds)
    {
        IProjectionStore memoryProj = (IProjectionStore)memoryReader;
        IProjectionStore sqliteProj = (IProjectionStore)sqliteReader;

        string?[] scopes = [null, .. harnessIds];
        int[] pages = [1, 2];
        foreach (string? harnessId in scopes)
        {
            foreach (int page in pages)
            {
                Assert.Equivalent(
                    memoryProj.SessionHeaderPage(harnessId, null, page, 3),
                    sqliteProj.SessionHeaderPage(harnessId, null, page, 3),
                    strict: true);
                Assert.Equivalent(
                    memoryProj.SessionHeaderPage(harnessId, "sess", page, 3),
                    sqliteProj.SessionHeaderPage(harnessId, "sess", page, 3),
                    strict: true);
            }
        }

        Assert.Equivalent(memoryProj.SessionHeadersByKeys(sessionKeys), sqliteProj.SessionHeadersByKeys(sessionKeys), strict: true);
        Assert.Empty(sqliteProj.SessionHeadersByKeys([]));

        foreach (string sessionKey in sessionKeys)
        {
            Assert.Equivalent(
                memoryProj.SessionChildHeaderPage(sessionKey, 1, 25),
                sqliteProj.SessionChildHeaderPage(sessionKey, 1, 25),
                strict: true);
        }

        IReadOnlyList<string> previewParents = [.. sessionKeys.Take(3), "sess-missing"];
        Assert.Equivalent(
            memoryProj.SessionChildPreview(previewParents, 25),
            sqliteProj.SessionChildPreview(previewParents, 25),
            strict: true);

        IReadOnlyList<SessionListFigure> memoryFigures = memoryReader.SessionListFigures();
        string[] keys = [.. memoryFigures.Select(static figure => figure.SessionKey)];
        Assert.Equivalent(memoryFigures, sqliteReader.SessionListFigures(keys), strict: true);
    }

    private static void AssertPairFilterParity(
        IRelationalStoreReader memoryReader,
        IRelationalStoreReader sqliteReader,
        string? spanId,
        string? sessionKey)
    {
        Assert.Equivalent(memoryReader.TokenCounts(spanId, sessionKey), sqliteReader.TokenCounts(spanId, sessionKey), strict: true);
        Assert.Equivalent(memoryReader.ReportedTotals(spanId, sessionKey), sqliteReader.ReportedTotals(spanId, sessionKey), strict: true);
        Assert.Equivalent(memoryReader.ReportedCosts(spanId, sessionKey), sqliteReader.ReportedCosts(spanId, sessionKey), strict: true);
        Assert.Equivalent(memoryReader.SkillsInvoked(spanId, sessionKey), sqliteReader.SkillsInvoked(spanId, sessionKey), strict: true);
        Assert.Equivalent(memoryReader.ToolCalls(spanId, sessionKey), sqliteReader.ToolCalls(spanId, sessionKey), strict: true);
        if (spanId is not null && sessionKey is not null)
        {
            Assert.Equivalent(
                memoryReader.TurnContentParts(sessionKey, spanId: spanId),
                sqliteReader.TurnContentParts(sessionKey, spanId: spanId),
                strict: true);
        }
        Assert.Equivalent(memoryReader.ToolDefinitions(spanId, sessionKey), sqliteReader.ToolDefinitions(spanId, sessionKey), strict: true);
        Assert.Equivalent(memoryReader.AgentInvocations(spanId, sessionKey), sqliteReader.AgentInvocations(spanId, sessionKey), strict: true);
        Assert.Equivalent(memoryReader.RecordSignals(spanId, sessionKey), sqliteReader.RecordSignals(spanId, sessionKey), strict: true);
    }

    private static void SeedPopulatedStores(SqliteRelationalStore sqlite, InMemoryRelationalStore memory)
    {
        string longPrompt = new string('p', 198) + "🚀tail";

        DateTimeOffset t0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t0.AddSeconds(5);
        DateTimeOffset t2 = t0.AddSeconds(10);

        CollectedBatch ccFileBatch = new(
            new CollectedSource("claude-code", SourceKind.File, "/logs/claude-1.jsonl"),
            [
                new CollectedSession(
                    SessionKey: "sess-proj",
                    HarnessId: "claude-code",
                    NativeSessionId: "native-proj",
                    Workspace: new CollectedWorkspace(null, "kyber-weave", "main"),
                    Title: "Proj\tSession\nLine2 🚀",
                    ReportedUsage:
                    [
                        new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Input, 180),
                        new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Output, 60),
                    ]),
                new CollectedSession(
                    SessionKey: "sess-unproj",
                    HarnessId: "claude-code",
                    NativeSessionId: "native-unproj",
                    Workspace: null,
                    Title: null,
                    ReportedUsage: []),
                new CollectedSession(
                    SessionKey: "sess-empty",
                    HarnessId: "claude-code",
                    NativeSessionId: "native-empty",
                    Workspace: null,
                    Title: "Empty",
                    ReportedUsage: []),
                new CollectedSession(
                    SessionKey: "sess-tokenless",
                    HarnessId: "claude-code",
                    NativeSessionId: "native-tokenless",
                    Workspace: null,
                    Title: "Tokenless",
                    ReportedUsage: []),
                new CollectedSession(
                    SessionKey: "sess-zero-tokens",
                    HarnessId: "claude-code",
                    NativeSessionId: "native-zero",
                    Workspace: null,
                    Title: "Zero",
                    ReportedUsage: []),
                new CollectedSession(
                    SessionKey: "sess-multi",
                    HarnessId: "claude-code",
                    NativeSessionId: "native-multi",
                    Workspace: new CollectedWorkspace(null, "multi-repo", "multi-branch"),
                    Title: "Multi",
                    ReportedUsage: []),
            ],
            [
                new CollectedRecord(
                    RecordId: "span-req-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.Unspecified,
                    Kind: RecordKind.Client,
                    Name: "request_1",
                    StartedAt: t0,
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-proj",
                    ParentRecordId: null,
                    TraceId: "trace-1",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: 6100,
                    Tokens: [],
                    ReportedCost: null,
                    Parts:
                    [
                        new CollectedContentPart(0, ContentBucket.UserPrompt, longPrompt, null, 45, null, "user", true, 220),
                        new CollectedContentPart(1, ContentBucket.ToolDefinitions, null, new CollectedWithheldText(500, 480, 120), 120, "fs", "system", false, null),
                    ],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions:
                    [
                        new CollectedToolDefinition(0, "read_file", "fs", 120),
                        new CollectedToolDefinition(1, "unused_tool", "mcp-db", 200),
                        new CollectedToolDefinition(2, "builtin_tool", null, 50),
                    ],
                    Signals: new CollectedRecordSignals("signal\twarn", 1, "corr\n1", 200000),
                    ReportedInputTokens: 350,
                    ReportedOutputTokens: 40),
                new CollectedRecord(
                    RecordId: "span-turn-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_call_1",
                    StartedAt: t0.AddMilliseconds(200),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-proj",
                    ParentRecordId: "span-req-1",
                    TraceId: "trace-1",
                    Model: "claude-sonnet-4-5",
                    EndedAt: t1,
                    ReportedDurationMs: 4800,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 100),
                        new CollectedTokenCount(TokenClass.CacheRead, 200),
                        new CollectedTokenCount(TokenClass.CacheWrite, 50),
                        new CollectedTokenCount(TokenClass.Output, 40),
                        new CollectedTokenCount(TokenClass.Reasoning, 10),
                    ],
                    ReportedCost: new CollectedCost(0.025, "USD"),
                    Parts:
                    [
                        new CollectedContentPart(0, ContentBucket.UserMessage, "Turn 1\tprompt", null, 12, null, "user", true, 13),
                    ],
                    Tool: new CollectedToolCall(
                        CallId: "call-1",
                        ToolName: "read_file",
                        McpServerName: "fs",
                        McpToolName: "read",
                        Input: new CollectedToolContent("path\t/tmp/a.txt", 14, 14, DummySha256, 12),
                        Result: new CollectedToolContent("ok\nline2", 8, 8, null, 8),
                        Succeeded: true,
                        Error: null,
                        ExitCode: 0),
                    Agent: null,
                    SkillsInvoked: ["csharp-dev"],
                    AgentInstanceId: "agent-inst-1",
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null),
                    ReportedInputTokens: 350,
                    ReportedOutputTokens: 40),
                new CollectedRecord(
                    RecordId: "span-req-2",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "request_2",
                    StartedAt: t2,
                    Status: RecordStatus.Error,
                    SessionKey: "sess-proj",
                    ParentRecordId: null,
                    TraceId: "trace-2",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: 1200,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 80),
                        new CollectedTokenCount(TokenClass.Output, 20),
                    ],
                    ReportedCost: null,
                    Parts:
                    [
                        new CollectedContentPart(0, ContentBucket.UserPrompt, null, new CollectedWithheldText(100, 90, 20), 20, null, "user", false, null),
                    ],
                    Tool: new CollectedToolCall(
                        CallId: "call-2",
                        ToolName: "ad_hoc_tool",
                        McpServerName: null,
                        McpToolName: null,
                        Input: null,
                        Result: null,
                        Succeeded: false,
                        Error: new CollectedError("boom\tmessage", "E_BOOM"),
                        ExitCode: 1),
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null),
                    ReportedInputTokens: 80,
                    ReportedOutputTokens: 25),
                new CollectedRecord(
                    RecordId: "span-agent-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.AgentInvoke,
                    Kind: RecordKind.Internal,
                    Name: "delegate_task",
                    StartedAt: t0.AddSeconds(1),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-proj",
                    ParentRecordId: "span-req-1",
                    TraceId: "trace-1",
                    Model: null,
                    EndedAt: t0.AddMilliseconds(1500),
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: new CollectedAgentInvocation("reviewer", "span-req-1", true, null),
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "span-aux-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.Unspecified,
                    Kind: RecordKind.Client,
                    Name: "xtabprovider",
                    StartedAt: t0.AddSeconds(2),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-proj",
                    ParentRecordId: null,
                    TraceId: "trace-aux",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: 250,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 30),
                        new CollectedTokenCount(TokenClass.Output, 10),
                    ],
                    ReportedCost: new CollectedCost(0.001, "USD"),
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "span-unproj-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_unproj_1",
                    StartedAt: t0.AddMinutes(1),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-unproj",
                    ParentRecordId: null,
                    TraceId: "trace-u1",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 50),
                        new CollectedTokenCount(TokenClass.Output, 15),
                    ],
                    ReportedCost: new CollectedCost(0.01, "USD"),
                    Parts:
                    [
                        new CollectedContentPart(0, ContentBucket.ConversationHistory, "First\thistory message", null, 5, null, "user", true, 20),
                    ],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "span-unproj-2",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_unproj_2",
                    StartedAt: t0.AddMinutes(2),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-unproj",
                    ParentRecordId: null,
                    TraceId: "trace-u2",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 60),
                        new CollectedTokenCount(TokenClass.Output, 20),
                    ],
                    ReportedCost: new CollectedCost(0.02, "EUR"),
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "span-unproj-agent",
                    HarnessId: "claude-code",
                    Op: RecordOp.AgentInvoke,
                    Kind: RecordKind.Internal,
                    Name: "subagent_run",
                    StartedAt: t0.AddMinutes(2).AddSeconds(5),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-unproj",
                    ParentRecordId: "span-unproj-1",
                    TraceId: "trace-u1",
                    Model: null,
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "span-unproj-aux",
                    HarnessId: "claude-code",
                    Op: RecordOp.Unspecified,
                    Kind: RecordKind.Client,
                    Name: "title",
                    StartedAt: t0.AddMinutes(2).AddSeconds(6),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-unproj",
                    ParentRecordId: null,
                    TraceId: "trace-u3",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 20),
                        new CollectedTokenCount(TokenClass.Output, 5),
                    ],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "span-tokenless-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_tokenless",
                    StartedAt: t0.AddMinutes(5),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-tokenless",
                    ParentRecordId: null,
                    TraceId: "trace-tl1",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens: [],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null),
                    ReportedInputTokens: null,
                    ReportedOutputTokens: null),
                new CollectedRecord(
                    RecordId: "span-zero-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_zero",
                    StartedAt: t0.AddMinutes(6),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-zero-tokens",
                    ParentRecordId: null,
                    TraceId: "trace-z1",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 0),
                        new CollectedTokenCount(TokenClass.Output, 0),
                    ],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
                new CollectedRecord(
                    RecordId: "span-multi-cc-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_multi_cc",
                    StartedAt: t0.AddMinutes(7),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-multi",
                    ParentRecordId: null,
                    TraceId: "trace-m1",
                    Model: "claude-sonnet-4-5",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 100),
                        new CollectedTokenCount(TokenClass.Output, 10),
                    ],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            [
                new CollectedProblem("prob-1", "warning", "P_WARN", "Warn\tmsg\n1", "span-turn-1", "line:1", "sess-proj", "claude-code", "2026-11-19T10:00:01.000Z", "stack\ttrace"),
                new CollectedProblem("prob-2", "error", "P_ERR", "Err msg", "span-req-2", null, "sess-proj", "claude-code", null, null),
            ]);

        // The multi-harness session's codex turn arrives under its own harness source; the
        // session row stays claude-code so the sessions list must still count both turns.
        CollectedBatch codexMultiBatch = new(
            new CollectedSource("codex", SourceKind.Otlp, "http://127.0.0.1:4318/v1/traces-codex-multi"),
            [],
            [
                new CollectedRecord(
                    RecordId: "span-multi-cx-1",
                    HarnessId: "codex",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_multi_cx",
                    StartedAt: t0.AddMinutes(8),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-multi",
                    ParentRecordId: null,
                    TraceId: "trace-m2",
                    Model: "gpt-5.1-codex",
                    EndedAt: null,
                    ReportedDurationMs: null,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 200),
                        new CollectedTokenCount(TokenClass.Output, 20),
                    ],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            []);

        CollectedBatch ccOtlpBatch = new(
            new CollectedSource("claude-code", SourceKind.Otlp, "http://127.0.0.1:4318/v1/traces"),
            [
                new CollectedSession("sess-unpriced", "claude-code", "native-unpriced", null, "Unpriced", []),
            ],
            [
                new CollectedRecord(
                    RecordId: "span-unpriced-1",
                    HarnessId: "claude-code",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "llm_unpriced",
                    StartedAt: t0.AddMinutes(3),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-unpriced",
                    ParentRecordId: null,
                    TraceId: "trace-up1",
                    Model: "custom-model-x",
                    EndedAt: t0.AddMinutes(3).AddSeconds(2),
                    ReportedDurationMs: null,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 100),
                        new CollectedTokenCount(TokenClass.Output, 25),
                    ],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [new CollectedToolDefinition(0, "dormant_tool", null, 50)],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            []);

        CollectedBatch copilotBatch = new(
            new CollectedSource("copilot", SourceKind.File, "/logs/copilot.jsonl"),
            [
                new CollectedSession(
                    SessionKey: "sess-copilot",
                    HarnessId: "copilot",
                    NativeSessionId: "native-cp",
                    Workspace: new CollectedWorkspace("/repo/cp", "cp-repo", null),
                    Title: "Copilot\tRun",
                    ReportedUsage: []),
            ],
            [
                new CollectedRecord(
                    RecordId: "span-cp-1",
                    HarnessId: "copilot",
                    Op: RecordOp.LlmInvoke,
                    Kind: RecordKind.Client,
                    Name: "cp_turn",
                    StartedAt: t0.AddMinutes(4),
                    Status: RecordStatus.Ok,
                    SessionKey: "sess-copilot",
                    ParentRecordId: null,
                    TraceId: "trace-cp1",
                    Model: "claude-sonnet-5-5",
                    EndedAt: t0.AddMinutes(4).AddSeconds(3),
                    ReportedDurationMs: 3000,
                    Tokens:
                    [
                        new CollectedTokenCount(TokenClass.Input, 200),
                        new CollectedTokenCount(TokenClass.CacheRead, 100),
                        new CollectedTokenCount(TokenClass.Output, 50),
                    ],
                    ReportedCost: null,
                    Parts: [],
                    Tool: null,
                    Agent: null,
                    SkillsInvoked: [],
                    AgentInstanceId: null,
                    ToolDefinitions: [new CollectedToolDefinition(0, "cp_tool", "mcp-cp", 300)],
                    Signals: new CollectedRecordSignals(null, null, null, null)),
            ],
            []);

        CollectedProblem standaloneProblem = new("prob-3", "warning", "STANDALONE", "Standalone\nproblem", "span-unpriced-1", null, "sess-unpriced", "claude-code", null, null);

        sqlite.WriteBatch(ccFileBatch);
        memory.WriteBatch(ccFileBatch);
        sqlite.WriteBatch(ccOtlpBatch);
        memory.WriteBatch(ccOtlpBatch);
        sqlite.WriteBatch(copilotBatch);
        memory.WriteBatch(copilotBatch);
        sqlite.WriteBatch(codexMultiBatch);
        memory.WriteBatch(codexMultiBatch);
        sqlite.WriteProblem(standaloneProblem);
        memory.WriteProblem(standaloneProblem);

        StoredSessionProjection[] projections =
        [
            new StoredSessionProjection(
                SessionId: "sess-proj",
                Label: "Projected\tSession",
                IsSubagent: false,
                SpanCount: 5,
                StartedAtUtc: t0.ToString("O"),
                EndedAtUtc: t2.ToString("O"),
                ParentSessionId: "sess-root",
                AgentName: "dev-hard",
                IncludedInParent: true,
                RequestSpanIds: ["span-req-1", "span-req-2"],
                Turns:
                [
                    new StoredSessionTurn("sess-proj", 0, "span-turn-1"),
                    new StoredSessionTurn("sess-proj", 1, "span-req-2"),
                ],
                Context: new StoredSessionContext(
                    ContextLimit: 200000,
                    ContextLimitSource: "model_table",
                    ResidualTotal: 150,
                    FreshJumpFactor: 1.5,
                    AccumulationRate: 0.3,
                    ObservedBuckets: ["user_prompt", "tool_definitions"],
                    DerivedCounts: [new StoredSessionContextDerivedCount("turns", 2)],
                    FlaggedTurnIndexes: [1],
                    Turns:
                    [
                        new StoredSessionContextTurn(
                            Position: 0,
                            InputTokens: 350,
                            BucketedTokens: 300,
                            ResidualTokens: 50,
                            ResidualAttribution: "unattributed",
                            BuiltinToolDefinitionTokens: 50,
                            FreshInput: 100,
                            Buckets: [new StoredTurnBucketTokens("user_prompt", 45)],
                            ServerDefinitions: [new StoredTurnServerDefinitionTokens("fs", 120)],
                            Headroom: 199650,
                            Pressure: 0.00175,
                            AccumulationRate: 0.2,
                            StrippedBlockCount: 1,
                            StrippedTokens: 20,
                            PreviousFreshInput: 0,
                            FreshJumpFactor: 1.0,
                            NonResidentTokens: 10),
                        new StoredSessionContextTurn(
                            Position: 1,
                            InputTokens: 80,
                            BucketedTokens: 70,
                            ResidualTokens: 10,
                            ResidualAttribution: "residual",
                            BuiltinToolDefinitionTokens: 0,
                            FreshInput: 80,
                            Buckets: [new StoredTurnBucketTokens("user_prompt", 20), new StoredTurnBucketTokens("tool_definitions", 50)],
                            ServerDefinitions: [],
                            Headroom: 199920,
                            Pressure: 0.0004),
                    ]),
                ContextUnmeasurableReason: null,
                Schema: new StoredSessionSchema(
                    InvocationCount: 2,
                    TurnCount: 2,
                    RankedTools:
                    [
                        new StoredSchemaRankedTool(0, "unused_tool", 400, false, "mcp-db"),
                        new StoredSchemaRankedTool(1, "read_file", 240, true, "fs"),
                    ],
                    Servers:
                    [
                        new StoredSchemaServerTokens("mcp-db", 400),
                        new StoredSchemaServerTokens("fs", 240),
                    ],
                    UnusedTokenResidencies: 400),
                SchemaUnmeasurableReason: null,
                TimelineSubagentSpanIds: ["span-agent-1"],
                TimelineAuxiliarySpanIds: ["span-aux-1"]),
            new StoredSessionProjection(
                SessionId: "sess-unpriced",
                Label: "Unpriced\nSession",
                IsSubagent: false,
                SpanCount: 1,
                StartedAtUtc: t0.AddMinutes(3).ToString("O"),
                EndedAtUtc: t0.AddMinutes(3).AddSeconds(2).ToString("O"),
                ParentSessionId: null,
                AgentName: null,
                IncludedInParent: false,
                RequestSpanIds: ["span-unpriced-1"],
                Turns: [new StoredSessionTurn("sess-unpriced", 0, "span-unpriced-1")],
                Context: null,
                ContextUnmeasurableReason: "missing context",
                Schema: new StoredSessionSchema(
                    InvocationCount: 0,
                    TurnCount: 1,
                    RankedTools: [new StoredSchemaRankedTool(0, "dormant_tool", 50, false, null)],
                    Servers: [],
                    UnusedTokenResidencies: 50),
                SchemaUnmeasurableReason: null,
                TimelineSubagentSpanIds: null,
                TimelineAuxiliarySpanIds: null),
            new StoredSessionProjection(
                SessionId: "sess-copilot",
                Label: "Copilot Projection",
                IsSubagent: false,
                SpanCount: 1,
                StartedAtUtc: t0.AddMinutes(4).ToString("O"),
                EndedAtUtc: t0.AddMinutes(4).AddSeconds(3).ToString("O"),
                ParentSessionId: null,
                AgentName: null,
                IncludedInParent: false,
                RequestSpanIds: ["span-cp-1"],
                Turns: [new StoredSessionTurn("sess-copilot", 0, "span-cp-1")],
                Context: null,
                ContextUnmeasurableReason: "not captured",
                Schema: new StoredSessionSchema(
                    InvocationCount: 0,
                    TurnCount: 1,
                    RankedTools: [new StoredSchemaRankedTool(0, "cp_tool", 300, false, "mcp-cp")],
                    Servers: [new StoredSchemaServerTokens("mcp-cp", 300)],
                    UnusedTokenResidencies: 300),
                SchemaUnmeasurableReason: null,
                TimelineSubagentSpanIds: null,
                TimelineAuxiliarySpanIds: null),
        ];

        sqlite.ReplaceSessionProjections(projections);
        memory.ReplaceSessionProjections(projections);

        StoredRun[] runs =
        [
            new StoredRun("run-cc", "claude-code", "explicit", "explicit_run_id", 2, "Run\tCC", "/work/cc", t0.ToString("O"), t2.ToString("O")),
            new StoredRun("run-cp", "copilot", "derived", "session_fallback", 1, null, null, null, null),
        ];
        sqlite.ReplaceRuns(runs);
        memory.ReplaceRuns(runs);

        StoredExecution[] executions =
        [
            new StoredExecution("exec-root", "run-cc", "claude-code", true, "sess-proj", null, "conductor", t0.ToString("O"), t2.ToString("O")),
            new StoredExecution("exec-child", "run-cc", "claude-code", false, "sess-unproj", "exec-root", "dev-hard", null, null),
            new StoredExecution("exec-cp", "run-cp", "copilot", true, "sess-copilot", null, null, null, null),
        ];
        sqlite.ReplaceExecutions(executions);
        memory.ReplaceExecutions(executions);

        StoredHarnessRollup[] rollups =
        [
            new StoredHarnessRollup("claude-code", 4, 1, 2, 1, 4, [new StoredHarnessRollupMetric("cache_hit_rate", 0.5, "measured")]),
            new StoredHarnessRollup("copilot", 1, 1, 1, 0, 1, []),
        ];
        sqlite.ReplaceHarnessRollups(rollups);
        memory.ReplaceHarnessRollups(rollups);

        StoredFinding[] findings =
        [
            new StoredFinding(
                FindingId: "find-1",
                DetectorId: "dormant-tool-schema",
                Title: "Dormant\tschema",
                Mechanism: "Unused schema\nrent",
                Confidence: "high",
                Recommendation: "Defer schema",
                OutcomeRiskCaveat: "Low risk",
                RankScore: 95.5,
                EvidenceLinks: [new FindingEvidenceLink("span-req-1", 0, "Offered tool")],
                EstimatedWasteTokens: 400,
                ErrorBar: new FindingErrorBar(300, 500),
                RunId: "run-cc",
                SessionId: "sess-proj",
                MeasurementClass: "measured",
                ConfidenceBasis: "exact schema tokens",
                WhatWouldRaiseIt: null,
                CompactionHazard: new StoredCompactionHazard(200000, "model_table")),
            new StoredFinding(
                FindingId: "find-2",
                DetectorId: "prefix-cache-break",
                Title: "Cache miss",
                Mechanism: "Prompt drift",
                Confidence: "medium",
                Recommendation: "Keep prefix stable",
                OutcomeRiskCaveat: "Medium",
                RankScore: 40.0,
                EvidenceLinks: [],
                RunId: "run-cp",
                SessionId: "sess-copilot"),
        ];
        sqlite.ReplaceFindings(findings);
        memory.ReplaceFindings(findings);

        StoredPrediction pred1 = new("pred-1", "find-1", "run-cc", 400, 0.85, "high", "2026-11-19T10:10:00.000Z", "run-cp", 380, 0.95);
        StoredPrediction pred2 = new("pred-2", "find-2", "run-cp", 120, 0.50, "medium", "2026-11-19T10:11:00.000Z");
        sqlite.ReplacePrediction(pred1);
        memory.ReplacePrediction(pred1);
        sqlite.ReplacePrediction(pred2);
        memory.ReplacePrediction(pred2);
    }

    private static string ToHex(string text) => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// A <see cref="SqliteRelationalStore"/> subclass recording each script together with the
    /// raw stdout it produced, so a test can prove text a read never needed was never
    /// materialized out of the database.
    /// </summary>
    private class CapturingSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly List<(string Script, string Output)> captured = [];

        public CapturingSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker)
        {
        }

        public IReadOnlyList<(string Script, string Output)> CapturedScripts => captured;

        public void ClearCapturedScripts() => captured.Clear();

        protected override string ExecuteSqliteCore(string sql)
        {
            string output = base.ExecuteSqliteCore(sql);
            captured.Add((sql, output));
            return output;
        }
    }

    private class ScriptedSqliteRelationalStore : SqliteRelationalStore
    {
        private readonly Queue<string> responses = new();

        public ScriptedSqliteRelationalStore(string databasePath, ICircuitBreaker circuitBreaker)
            : base(databasePath, circuitBreaker, initialize: false)
        {
        }

        public void EnqueueResponse(string response) => responses.Enqueue(response);

        protected override string ExecuteSqliteCore(string sql) =>
            responses.Count > 0 ? responses.Dequeue() : string.Empty;
    }

    private class OverridePricingLookup : IModelPricingLookup
    {
        private readonly string modelId;
        private readonly ModelCosts costs;

        public OverridePricingLookup(string modelId, ModelCosts costs)
        {
            this.modelId = modelId;
            this.costs = costs;
        }

        public ModelCosts? GetModelCosts(string model) =>
            string.Equals(model, modelId, StringComparison.Ordinal) ? costs : null;
    }
}

/// <summary>
/// <see cref="IModelPricingLookup"/> and <see cref="PricingTables"/> overloads on
/// <see cref="CanonPricing"/>: custom tables, custom namespaces, interface dispatch, the
/// process-wide fallback, and harness-cost preservation.
/// </summary>
/// <remarks>
/// The null-lookup overload reads the process-wide pricing tables, which parallel test classes
/// reconfigure per test — and <c>GetTables</c> evaluates a provider under its gate, so a
/// mid-read install swaps the tables being served. The collection therefore runs while no other
/// collection does, and the test installs tables it owns for the fallback assertion and restores
/// the provider it found.
/// </remarks>
[Collection(CanonPricingStatics.Name)]
public class CanonPricingOverloadsTests
{
    /// <summary>
    /// Verifies <see cref="IModelPricingLookup"/> and <see cref="PricingTables"/> overloads on
    /// <see cref="CanonPricing"/> resolve custom tables, custom namespaces, interface dispatch,
    /// fallback defaults, and harness-cost preservation.
    /// </summary>
    [Fact]
    public void CanonPricingOverloads_SupportCustomPricingTablesAndIModelPricingLookup()
    {
        ModelCosts customRates = new(
            InputCostPerToken: 0.000002,
            OutputCostPerToken: 0.000010,
            CacheWriteCostPerToken: 0.0000025,
            CacheReadCostPerToken: 0.0000002,
            WebSearchCostPerRequest: 0.01,
            FastMultiplier: 1.0,
            CacheWriteCostIsExplicit: true);

        Dictionary<string, ModelCosts> cache = new(StringComparer.Ordinal)
        {
            ["custom-sonnet"] = customRates,
        };
        Dictionary<string, ModelCosts> lowercase = new(StringComparer.Ordinal)
        {
            ["custom-sonnet"] = customRates,
        };
        HashSet<string> namespaces = new(StringComparer.Ordinal)
        {
            "mygw",
        };
        PricingTables customTables = new(cache, ["custom-sonnet"], lowercase, namespaces);
        IModelPricingLookup customLookup = new OverridePricingLookup("override-model", customRates);

        Assert.Equal(customRates, CanonPricing.GetModelCosts("mygw/custom-sonnet", customTables));
        Assert.Equal(customRates, CanonPricing.GetModelCosts("mygw/custom-sonnet", (IModelPricingLookup)customTables));
        Assert.Null(CanonPricing.GetModelCosts("claude-sonnet-4-5", customTables));

        Assert.Equal(customRates, CanonPricing.GetModelCosts("override-model", customLookup));
        Assert.Null(CanonPricing.GetModelCosts("custom-sonnet", customLookup));

        // The null-lookup overload falls back to the process-wide pricing tables, which no other
        // test in this class configures: this test installs its own tables for that one dispatch
        // and restores the provider it found. ConfigurePricingProvider rejects null, so the
        // restore writes the private provider fields back directly.
        (Func<PricingTables>? Provider, PricingTables? Cache) saved = SaveProcessPricingProvider();
        try
        {
            CanonPricing.ConfigurePricingProvider(() => new PricingTables(
                new Dictionary<string, ModelCosts>(StringComparer.Ordinal) { ["claude-sonnet-4-5"] = customRates },
                ["claude-sonnet-4-5"],
                new Dictionary<string, ModelCosts>(StringComparer.Ordinal) { ["claude-sonnet-4-5"] = customRates },
                new HashSet<string>(StringComparer.Ordinal)));
            Assert.NotNull(CanonPricing.GetModelCosts("claude-sonnet-4-5", (IModelPricingLookup?)null));
        }
        finally
        {
            RestoreProcessPricingProvider(saved);
        }

        double directCost = CanonPricing.CalculateCost(
            "override-model",
            inputTokens: 1000,
            outputTokens: 500,
            cacheCreationTokens: 0,
            cacheReadTokens: 0,
            webSearchRequests: 0,
            fast: false,
            oneHourCacheCreationTokens: 0,
            pricing: customLookup);
        Assert.Equal((1000 * 0.000002) + (500 * 0.000010), directCost, precision: 12);

        TokenUsage usage = new(
            FreshInput: 1000,
            CacheRead: 200,
            CacheCreation: 100,
            Output: 500,
            ReportedInput: 1300,
            ReportedOutput: 500);

        CostBlock publishedViaTables = CanonPricing.PricePublishedTurn(usage, "mygw/custom-sonnet", "claude-code", customTables);
        Assert.Equal(CostBasis.Published, publishedViaTables.Basis);
        Assert.Equal(CostStatus.Priced, publishedViaTables.Status);
        Assert.Equal("USD", publishedViaTables.Currency);
        double expectedTurnCost = (1000 * 0.000002) + (500 * 0.000010) + (100 * 0.0000025) + (200 * 0.0000002);
        Assert.NotNull(publishedViaTables.Value);
        Assert.Equal(expectedTurnCost, publishedViaTables.Value.Value, precision: 12);

        CostBlock noRateTurn = CanonPricing.PriceTurn(usage, "claude-sonnet-4-5", "claude-code", customLookup);
        Assert.Equal(CostBasis.Published, noRateTurn.Basis);
        Assert.Equal(CostStatus.NoRate, noRateTurn.Status);
        Assert.Null(noRateTurn.Value);

        CostBlock harnessExisting = new(CostBasis.Harness, CostStatus.Priced, 0.42, "USD");
        CostBlock preserved = CanonPricing.PriceTurn(usage, "override-model", "claude-code", customLookup, harnessExisting);
        Assert.Equal(harnessExisting, preserved);
    }

    /// <summary>
    /// Reads the process-wide pricing provider state so this class can install its own tables
    /// and put the process back exactly as it found it.
    /// </summary>
    /// <returns>The configured provider and cached tables, either possibly <see langword="null"/>.</returns>
    private static (Func<PricingTables>? Provider, PricingTables? Cache) SaveProcessPricingProvider() => (
        PricingField("_pricingProvider").GetValue(null) as Func<PricingTables>,
        PricingField("_cachedTables").GetValue(null) as PricingTables);

    /// <summary>Restores pricing provider state saved by <see cref="SaveProcessPricingProvider"/>.</summary>
    /// <param name="saved">The provider and cached tables to write back.</param>
    private static void RestoreProcessPricingProvider((Func<PricingTables>? Provider, PricingTables? Cache) saved)
    {
        PricingField("_pricingProvider").SetValue(null, saved.Provider);
        PricingField("_cachedTables").SetValue(null, saved.Cache);
    }

    private static FieldInfo PricingField(string name) =>
        typeof(CanonPricing).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"CanonPricing.{name} is no longer a private static field.");

    private class OverridePricingLookup : IModelPricingLookup
    {
        private readonly string modelId;
        private readonly ModelCosts costs;

        public OverridePricingLookup(string modelId, ModelCosts costs)
        {
            this.modelId = modelId;
            this.costs = costs;
        }

        public ModelCosts? GetModelCosts(string model) =>
            string.Equals(model, modelId, StringComparison.Ordinal) ? costs : null;
    }
}

/// <summary>
/// The collection shared by tests that read <see cref="CanonPricing"/>'s process-wide tables and by
/// tests that reconfigure the provider: it runs exclusively, so no parallel class can swap the
/// tables mid-read, and every writer restores what it found.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class CanonPricingStatics
{
    /// <summary>The name tests use to join the exclusive pricing-statics collection.</summary>
    public const string Name = "canon-pricing-statics";
}
