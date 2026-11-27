using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KyberDash.Domain;

/// <summary>Per-token pricing rates and cache multipliers for one published model entry.</summary>
public record ModelCosts(
    double InputCostPerToken,
    double OutputCostPerToken,
    double CacheWriteCostPerToken,
    double CacheReadCostPerToken,
    double WebSearchCostPerRequest,
    double FastMultiplier,
    bool CacheWriteCostIsExplicit);

/// <summary>Input-size pricing tier in a rate table (inclusive <paramref name="UpTo"/> bound).</summary>
public record RateTier(
    double UpTo,
    double InputRate,
    double OutputRate,
    double? CacheReadRate = null,
    double? CacheWriteRate = null);

/// <summary>Per-model rate entry in a <see cref="RateTable"/>.</summary>
public record TableRate(
    bool Billed,
    double InputRate = 0,
    double OutputRate = 0,
    double? CacheReadRate = null,
    double? CacheWriteRate = null,
    IReadOnlyList<RateTier>? Tiers = null);

/// <summary>Published rate table scoped by harness applicability and optional per-model entries.</summary>
public record RateTable(
    string Name,
    string? Currency = null,
    IReadOnlyList<RateTier>? Tiers = null,
    IReadOnlyList<string>? Applicability = null,
    string? Model = null,
    IReadOnlyDictionary<string, TableRate>? PublishedRates = null);

/// <summary>Outcome of totalling multiple <see cref="CostBlock"/> instances under a single basis.</summary>
public record CostTotalResult(
    bool Ok,
    CostBlock? Total = null,
    Problem? Problem = null);

/// <summary>Recorded mismatch when a session mixes incompatible cost bases.</summary>
public record CostBasisMismatch(
    string Code,
    string Message,
    IReadOnlyList<string> Bases,
    IReadOnlyDictionary<string, double?> TotalsByBasis);

/// <summary>Context window limit together with its provenance source.</summary>
public record ContextWindowResult(
    long ContextLimit,
    string ContextLimitSource);

/// <summary>Derived availability of an analysis metric from a measurability map.</summary>
public record AvailabilityResult(
    bool IsMeasured,
    string? Availability = null,
    string? Reason = null);

/// <summary>Inputs considered when synthesizing a display title for a session.</summary>
public record SessionTitleInput(
    string? AgentName = null,
    string? TaskSummary = null,
    string? RawLabel = null,
    string? Label = null,
    string? Harness = null,
    string? SessionId = null);

/// <summary>
/// Canonical pricing, context-window derivation, harness normalization, and session naming shared
/// across KyberDash analysis modules, mirroring <c>dash/src/canon/cost.ts</c>,
/// <c>dash/src/canon/copilot-rates.ts</c>, <c>dash/src/canon/published-pricing.ts</c>,
/// <c>dash/src/pricing/models.ts</c>, <c>dash/src/canon/context-window.ts</c>,
/// <c>dash/src/canon/measurability.ts</c>, and <c>dash/src/canon/naming.ts</c>.
/// </summary>
public static partial class CanonPricing
{
    /// <summary>Token count per-million rates are quoted against.</summary>
    public const double TokensPerMillion = 1_000_000.0;

    /// <summary>Named default context window when telemetry neither reports nor declares one.</summary>
    public const long DefaultContextLimit = 200_000L;

    /// <summary>
    /// Inclusive prompt-token threshold above which <c>gpt-6-luna</c> switches to its high pricing tier
    /// (<c>dash/src/pricing/models.ts</c>).
    /// </summary>
    public const long Gpt6LunaPromptTokenThreshold = 272_000L;

    /// <summary>Inclusive prompt-token threshold at which <c>grok-4.6</c> switches to its high pricing tier.</summary>
    public const long Grok46PromptTokenThreshold = 200_000L;

    /// <summary>Standard-to-fast multiplier when a model does not specify its own.</summary>
    public const double OneHourCacheWriteMultiplier = 1.6;

    /// <summary>Flat per-request cost billed for web search tool calls.</summary>
    public const double WebSearchCost = 0.01;

    /// <summary>Problem code emitted when cost blocks span multiple billing bases.</summary>
    public const string CostBasisMismatchCode = "COST_BASIS_MISMATCH";

    /// <summary>Problem code emitted when cost blocks under one basis mix currencies.</summary>
    public const string CostCurrencyMismatchCode = "CURRENCY_MISMATCH";

    /// <summary>Raw attribute key holding a harness-declared (non-telemetry) context window.</summary>
    public const string DeclaredContextLimitKey = "declaredContextWindow";

    /// <summary>Ordered attribute keys checked when resolving a turn's model identity.</summary>
    public static IReadOnlyList<string> ModelIdentityKeys { get; } =
    [
        "gen_ai.response.model",
        "gen_ai.request.model",
        "model",
        "opencode.model.id",
    ];

    /// <summary>Ordered attribute keys checked when resolving a telemetry-reported context window.</summary>
    public static IReadOnlyList<string> ContextLimitKeys { get; } =
    [
        "contextWindow",
        "gen_ai.request.max_context_tokens",
        "gen_ai.request.context_window",
        "model_context_window",
        "opencode.model.context_limit",
    ];

    private static readonly IReadOnlySet<string> ReasoningIncludedInOutput =
        new HashSet<string>(["claude", "codex", "copilot"], StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> PublishedApplicabilityHarnesses =
        new HashSet<string>(
        [
            "claude-code",
            "claude-cli",
            "claude-desktop",
            "claude-unclassified",
            "claude",
            "codex",
            "codex-cli",
            "codex-desktop",
            "codex-unclassified",
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> ExcludedHarnessIdentities =
        new HashSet<string>(["gemini"], StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> SurveyFamilyMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["antigravity-cli"] = "antigravity",
            ["antigravity-ide"] = "antigravity",
            ["copilot-cli"] = "copilot",
            ["copilot-vscode"] = "copilot",
            ["copilot-jetbrains"] = "copilot",
            ["copilot-agent"] = "copilot",
            ["cline-cli"] = "cline",
            ["claude-cli"] = "claude-code",
            ["claude-unclassified"] = "claude-code",
            ["codex-cli"] = "codex",
            ["codex-desktop"] = "codex",
            ["codex-unclassified"] = "codex",
            ["kilo-shared-runtime"] = "kilo-code",
            ["kilo-vscode-legacy"] = "kilo-code",
        };

    private static readonly IReadOnlyDictionary<string, string> BuiltinAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["anthropic--claude-4.6-opus"] = "claude-opus-4-6",
            ["anthropic--claude-4.6-sonnet"] = "claude-sonnet-4-6",
            ["anthropic--claude-4.5-opus"] = "claude-opus-4-5",
            ["anthropic--claude-4.5-sonnet"] = "claude-sonnet-4-5",
            ["anthropic--claude-4.5-haiku"] = "claude-haiku-4-5",
            ["claude-haiku-4.5"] = "claude-haiku-4-5",
            ["claude-sonnet-4.6"] = "claude-sonnet-4-6",
            ["claude-sonnet-4.5"] = "claude-sonnet-4-5",
            ["claude-opus-4.7"] = "claude-opus-4-7",
            ["claude-opus-4.6"] = "claude-opus-4-6",
            ["claude-opus-4.5"] = "claude-opus-4-5",
            ["cursor-auto"] = "claude-sonnet-4-5",
            ["cursor-agent-auto"] = "claude-sonnet-4-5",
            ["copilot-auto"] = "claude-sonnet-4-5",
            ["copilot-openai-auto"] = "gpt-5.3-codex",
            ["copilot-anthropic-auto"] = "claude-sonnet-4-5",
            ["openai-codex:gpt-5.5"] = "gpt-5.5",
            ["ibm-bob-auto"] = "claude-sonnet-4-5",
            ["kiro-auto"] = "claude-sonnet-4-5",
            ["quickdesk-auto"] = "claude-sonnet-4-5",
            ["cline-auto"] = "claude-sonnet-4-5",
            ["openclaw-auto"] = "claude-sonnet-4-5",
            ["warp-auto-efficient"] = "gpt-5.3-codex",
            ["warp-auto-powerful"] = "claude-opus-4-6",
            ["codex-auto-review"] = "gpt-5.5",
            ["grok-build"] = "grok-build-0.1",
            ["GPT-5.3 Codex (low reasoning)"] = "gpt-5.3-codex",
            ["GPT-5.3 Codex (medium reasoning)"] = "gpt-5.3-codex",
            ["GPT-5.3 Codex (high reasoning)"] = "gpt-5.3-codex",
            ["GPT-5.3 Codex (extra high reasoning)"] = "gpt-5.3-codex",
            ["Claude Sonnet 4.6"] = "claude-sonnet-4-6",
            ["Claude Sonnet 4.5"] = "claude-sonnet-4-5",
            ["Claude Haiku 4.5"] = "claude-haiku-4-5",
            ["Claude Opus 4.6"] = "claude-opus-4-6",
            ["claude-4-6-sonnet-high"] = "claude-sonnet-4-6",
            ["claude-4-6-sonnet-low"] = "claude-sonnet-4-6",
            ["claude-4-6-sonnet-medium"] = "claude-sonnet-4-6",
            ["claude-4-6-sonnet-high-fast"] = "claude-sonnet-4-6",
            ["claude-4-7-opus-xhigh"] = "claude-opus-4-7",
            ["claude-4-7-opus-xhigh-fast"] = "claude-opus-4-7",
            ["qwen-auto"] = "claude-sonnet-4-5",
            ["orcarouter/fusion"] = "openai/gpt-oss-120b",
            ["orcarouter/fusion-flash"] = "openai/gpt-oss-120b",
            ["orcarouter/fusion-mini"] = "openai/gpt-oss-120b",
            ["kimi-auto"] = "kimi-k2-thinking",
            ["kimi-code"] = "kimi-k2-thinking",
            ["kimi-for-coding"] = "kimi-k2-thinking",
            ["k3"] = "kimi-k3",
            ["k3-agent"] = "kimi-k3",
            ["k2d6-agent"] = "kimi-k2p6",
            ["mimo-v2-flash"] = "xiaomi/mimo-v2-flash",
            ["mimo-v2.5-pro"] = "xiaomi/mimo-v2.5-pro",
            ["mimo-v2.5"] = "xiaomi/mimo-v2.5",
            ["kat-coder-pro-v1"] = "kwaipilot/kat-coder-pro",
            ["claude-4-sonnet"] = "claude-sonnet-4",
            ["claude-4-sonnet-1m"] = "claude-sonnet-4",
            ["claude-4-sonnet-thinking"] = "claude-sonnet-4",
            ["claude-4.5-sonnet"] = "claude-sonnet-4-5",
            ["claude-4.5-sonnet-thinking"] = "claude-sonnet-4-5",
            ["claude-4.6-sonnet"] = "claude-sonnet-4-6",
            ["claude-4.6-sonnet-high"] = "claude-sonnet-4-6",
            ["claude-4.6-sonnet-low"] = "claude-sonnet-4-6",
            ["claude-4.6-sonnet-thinking"] = "claude-sonnet-4-6",
            ["claude-4.6-sonnet-high-thinking"] = "claude-sonnet-4-6",
            ["claude-4-opus"] = "claude-opus-4",
            ["claude-4.5-opus"] = "claude-opus-4-5",
            ["claude-4.5-opus-high"] = "claude-opus-4-5",
            ["claude-4.5-opus-low"] = "claude-opus-4-5",
            ["claude-4.5-opus-medium"] = "claude-opus-4-5",
            ["claude-4.5-opus-high-thinking"] = "claude-opus-4-5",
            ["claude-4.6-opus"] = "claude-opus-4-6",
            ["claude-4.6-opus-fast-mode"] = "claude-opus-4-6",
            ["claude-4.6-opus-high"] = "claude-opus-4-6",
            ["claude-4.6-opus-low"] = "claude-opus-4-6",
            ["claude-4.6-opus-medium"] = "claude-opus-4-6",
            ["claude-4.6-opus-high-thinking"] = "claude-opus-4-6",
            ["claude-4.7-opus"] = "claude-opus-4-7",
            ["claude-opus-4-7-thinking-high"] = "claude-opus-4-7",
            ["claude-4.5-haiku"] = "claude-haiku-4-5",
            ["claude-4.6-haiku"] = "claude-haiku-4-5",
            ["gpt-5-fast"] = "gpt-5",
            ["gpt-4.1"] = "gpt-4.1",
            ["gpt-5.2-low"] = "gpt-5",
            ["gpt-5.1-codex-high"] = "gpt-5.3-codex",
            ["gemini-3.1-pro"] = "gemini-3.1-pro-preview",
            ["gemini-3-flash"] = "gemini-3-flash-preview",
            ["gemini-3.1-pro-high"] = "gemini-3.1-pro-preview",
            ["gemini-3.1-pro-low"] = "gemini-3.1-pro-preview",
            ["gemini-3-flash-agent"] = "gemini-3-flash-preview",
            ["gemini-3.5-flash-high"] = "gemini-3.5-flash",
            ["gemini-3.5-flash-medium"] = "gemini-3.5-flash",
            ["gemini-3.5-flash-low"] = "gemini-3.5-flash",
            ["Gemini 3.5 Flash (High)"] = "gemini-3.5-flash",
            ["Gemini 3.5 Flash (Medium)"] = "gemini-3.5-flash",
            ["Gemini 3.5 Flash (Low)"] = "gemini-3.5-flash",
            ["gemini-3-pro"] = "gemini-3-pro-preview",
            ["gemini-3.1-flash-image"] = "gemini-3.1-flash-image-preview",
            ["gemini-3.1-flash-lite"] = "gemini-3.1-flash-lite-preview",
            ["GLM-5.2"] = "glm-5p1",
            ["glm-5.2"] = "glm-5p1",
            ["GLM-5.3"] = "glm-5p2",
            ["glm-5.3"] = "glm-5p2",
        };

    private static readonly IReadOnlyDictionary<string, string> LowercaseBuiltinAliases =
        BuildLowercaseAliasIndex();

    private static readonly ModelCosts Grok46HighPromptCosts = BuildCosts(4e-6, 12e-6, null, 1e-6, null);
    private static readonly ModelCosts Gpt6LunaHighPromptCosts = BuildCosts(2e-7, 7.5e-7, null, 2e-8, null);

    private static readonly string[] ExtraNamespaces =
    [
        "cp", "cline-pass", "cline-free", "cmd", "antigravity", "orcarouter",
        "litellm_proxy", "openai_like",
        "zhipu", "mimo", "kimi",
    ];

    private static readonly Lock TablesGate = new();
    private static Func<PricingTables>? _pricingProvider;
    private static PricingTables? _cachedTables;

    /// <summary>Configures the delegate supplying pricing snapshot tables from the data layer.</summary>
    public static void ConfigurePricingProvider(Func<PricingTables> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (TablesGate)
        {
            _pricingProvider = provider;
            _cachedTables = null;
        }
    }

    private static PricingTables GetTables()
    {
        lock (TablesGate)
        {
            _cachedTables ??= _pricingProvider is not null ? _pricingProvider() : LoadEmbeddedPricingTables();
            return _cachedTables;
        }
    }

    /// <summary>GitHub Copilot AI credits table (<c>1 credit = $0.01</c>, USD per 1M tokens).</summary>
    public static RateTable CopilotCreditsTable { get; } = new(
        Name: "github-copilot-credits",
        Currency: "USD",
        Tiers: [],
        Applicability: ["copilot"],
        PublishedRates: new Dictionary<string, TableRate>(StringComparer.Ordinal)
        {
            ["claude-sonnet-5-5"] = new(true, 2, 10, 0.2, 2.5),
            ["claude-opus-5"] = new(true, 5, 25, 0.5, 6.25),
            ["gpt-6-luna"] = new(
                true,
                0.1,
                0.5,
                0.01,
                0.1,
                Tiers:
                [
                    new RateTier(Gpt6LunaPromptTokenThreshold, 0.1, 0.5, 0.01, 0.1),
                    new RateTier(double.PositiveInfinity, 0.2, 0.75, 0.02, 0.2),
                ]),
        });

    [GeneratedRegex(@"@.*$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AtSuffixRegex();

    [GeneratedRegex(@"-\d{8}$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DateSuffixRegex();

    [GeneratedRegex(@"\[[^\]]*\]$", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BracketSuffixRegex();

    [GeneratedRegex(@"^(?:omniroute:|cp/|cline-pass/|cline-free/|cmd/|antigravity/|orcarouter/)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RouterPrefixRegex();

    [GeneratedRegex(@":(thinking|cloud)$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex VariantColonSuffixRegex();

    [GeneratedRegex(@"-TEE$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex VariantTeeSuffixRegex();

    [GeneratedRegex(@"^(?:grok composer|warp auto)\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BuiltInFlatRateDisplayRegex();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UuidExactRegex();

    [GeneratedRegex(@"\.(?:llm_request|llm_invoke|generate|completion)$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SpanOpRegex();

    [GeneratedRegex(@"^chat\s+(?:\S*(?:[_.:/]|-\d|[A-Za-z]\d|\d[A-Za-z])|.*\d\.\d)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ChatModelRegex();

    [GeneratedRegex(@"^(?:synth|derived):", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SynthPrefixRegex();

    [GeneratedRegex(@"^opencode\.(?:request|step|tool)$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex OpenCodeSpanNameRegex();

    [GeneratedRegex(@"^factory\.(?:session|turn|tool)$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FactorySpanNameRegex();

    [GeneratedRegex(@"^droid:\S+$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DroidSynthNameRegex();

    [GeneratedRegex(@"<(?:synthetic|unknown|unspecified)>|(?::|<)(?:synthetic|unknown)(?:>|$)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UnspecifiedModelRegex();

    /// <summary>Converts ASCII uppercase characters to lowercase without triggering locale warnings.</summary>
    internal static string ToAsciiLower(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        bool hasUpper = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is >= 'A' and <= 'Z')
            {
                hasUpper = true;
                break;
            }
        }

        if (!hasUpper)
            return value;

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char ch = source[i];
                span[i] = ch is >= 'A' and <= 'Z' ? (char)(ch + ('a' - 'A')) : ch;
            }
        });
    }

    /// <summary>Sum of the three disjoint input classes (<c>fresh + cacheRead + cacheCreation</c>).</summary>
    public static long MeasuredInput(TokenUsage tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        return tokens.FreshInput + tokens.CacheRead + tokens.CacheCreation;
    }

    /// <summary>
    /// Output tokens billed and displayed for one call (providers that already include reasoning
    /// inside <paramref name="outputTokens"/> do not add <paramref name="reasoningTokens"/> on top).
    /// </summary>
    public static long BillableOutputTokens(string provider, long outputTokens, long reasoningTokens)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return ReasoningIncludedInOutput.Contains(provider)
            ? outputTokens
            : outputTokens + reasoningTokens;
    }

    /// <summary>Selects the tightest pricing tier whose inclusive <c>UpTo</c> bound covers <paramref name="inputTokens"/>.</summary>
    public static RateTier? SelectTier(IReadOnlyList<RateTier> tiers, double inputTokens)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        RateTier? tightest = null;
        foreach (RateTier tier in tiers)
        {
            if (inputTokens <= tier.UpTo && (tightest is null || tier.UpTo < tightest.UpTo))
                tightest = tier;
        }

        return tightest;
    }

    /// <summary>True when <paramref name="table"/> applies to <paramref name="harness"/>.</summary>
    public static bool IsHarnessInScope(RateTable table, string? harness)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Applicability is null)
            return true;

        if (harness is null)
            return false;

        foreach (string allowed in table.Applicability)
        {
            if (string.Equals(allowed, harness, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Prices one (<paramref name="harness"/>, <paramref name="model"/>) turn against <paramref name="table"/>.</summary>
    public static CostBlock PriceWithTable(TokenUsage tokens, string? model, string? harness, RateTable table)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(table);

        if (!IsHarnessInScope(table, harness))
            return new CostBlock(CostBasis.Published, CostStatus.OutOfScope);

        RateTier? rate = null;
        if (table.PublishedRates is not null)
        {
            if (model is null || !table.PublishedRates.TryGetValue(model, out TableRate? tableRate))
                return new CostBlock(CostBasis.Published, CostStatus.NoRate);

            if (!tableRate.Billed)
                return new CostBlock(CostBasis.Published, CostStatus.NotBilled);

            long measuredInput = MeasuredInput(tokens);
            if (tableRate.Tiers is not null)
            {
                rate = SelectTier(tableRate.Tiers, measuredInput);
            }
            else
            {
                rate = new RateTier(double.PositiveInfinity, tableRate.InputRate, tableRate.OutputRate, tableRate.CacheReadRate, tableRate.CacheWriteRate);
            }
        }
        else
        {
            if (table.Model is not null && !string.Equals(table.Model, model, StringComparison.Ordinal))
                return new CostBlock(CostBasis.Published, CostStatus.NoRate);

            rate = table.Tiers is not null ? SelectTier(table.Tiers, MeasuredInput(tokens)) : null;
        }

        if (rate is null)
            return new CostBlock(CostBasis.Published, CostStatus.NoRate);

        double cacheReadRate = rate.CacheReadRate ?? rate.InputRate;
        double cacheWriteRate = rate.CacheWriteRate ?? rate.InputRate;
        double value =
            ((tokens.FreshInput * rate.InputRate) +
             (tokens.CacheRead * cacheReadRate) +
             (tokens.CacheCreation * cacheWriteRate) +
             (tokens.Output * rate.OutputRate)) / TokensPerMillion;

        IReadOnlyDictionary<string, double>? byModel = model is not null
            ? new Dictionary<string, double>(StringComparer.Ordinal) { [model] = value }
            : null;

        return new CostBlock(
            CostBasis.Published,
            CostStatus.Priced,
            value,
            table.Currency ?? "USD",
            byModel);
    }

    /// <summary>Produces a <see cref="CostBlock"/> preserving harness-reported values over table lookups.</summary>
    public static CostBlock CreateCostBlock(
        double? harnessReportedValue = null,
        string? harnessReportedCurrency = null,
        RateTable? table = null,
        TokenUsage? tokens = null,
        string? model = null,
        string? harness = null)
    {
        if (harnessReportedValue.HasValue && harnessReportedCurrency is not null)
        {
            return new CostBlock(
                CostBasis.Harness,
                CostStatus.Priced,
                harnessReportedValue.Value,
                harnessReportedCurrency);
        }

        if (table is not null && tokens is not null)
            return PriceWithTable(tokens, model, harness, table);

        return new CostBlock(CostBasis.Unknown, CostStatus.NoRate);
    }

    /// <summary>Formats a <see cref="CostBlock"/> as currency when valued, or in honest status words when unpriced.</summary>
    public static string RenderCost(CostBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Value is double value && double.IsFinite(value) && block.Currency is not null)
        {
            CultureInfo enUs = CultureInfo.GetCultureInfo("en-US");
            if (string.Equals(block.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                return value.ToString("C2", enUs);

            string sign = value < 0 ? "-" : string.Empty;
            double absValue = Math.Abs(value);
            return string.Equals(block.Currency, "EUR", StringComparison.OrdinalIgnoreCase)
                ? string.Create(CultureInfo.InvariantCulture, $"{sign}€{absValue:N2}")
                : string.Equals(block.Currency, "GBP", StringComparison.OrdinalIgnoreCase)
                    ? string.Create(CultureInfo.InvariantCulture, $"{sign}£{absValue:N2}")
                    : string.Create(CultureInfo.InvariantCulture, $"{sign}{block.Currency} {absValue:N2}");
        }

        return block.Status switch
        {
            CostStatus.NotBilled => "not billed",
            CostStatus.OutOfScope => "out of scope",
            _ => "no published rate",
        };
    }

    /// <summary>Totals a collection of cost blocks while refusing to blend distinct bases or currencies.</summary>
    public static CostTotalResult SumCosts(IReadOnlyList<CostBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
            return new CostTotalResult(true, new CostBlock(CostBasis.Unknown, CostStatus.NoRate));

        HashSet<CostBasis> bases = [];
        foreach (CostBlock block in blocks)
            bases.Add(block.Basis);

        if (bases.Count > 1)
        {
            List<string> sortedBases = [.. bases.Select(static b => b.ToWireString()).OrderBy(static s => s, StringComparer.Ordinal)];
            return new CostTotalResult(
                false,
                Problem: new Problem(
                    ProblemSeverity.Error,
                    CostBasisMismatchCode,
                    $"cost bases differ across blocks ({string.Join(", ", sortedBases)}); refusing to blend them into one total"));
        }

        CostBasis basis = blocks[0].Basis;
        List<CostBlock> valued = [];
        HashSet<string> currencies = new(StringComparer.Ordinal);
        foreach (CostBlock block in blocks)
        {
            if (block.Value is double v && double.IsFinite(v))
            {
                valued.Add(block);
                if (block.Currency is not null)
                    currencies.Add(block.Currency);
            }
        }

        if (currencies.Count > 1)
        {
            List<string> sortedCurrencies = [.. currencies.OrderBy(static c => c, StringComparer.Ordinal)];
            return new CostTotalResult(
                false,
                Problem: new Problem(
                    ProblemSeverity.Error,
                    CostCurrencyMismatchCode,
                    $"cost figures under basis \"{basis.ToWireString()}\" are in different currencies ({string.Join(", ", sortedCurrencies)}); refusing to blend them into one total"));
        }

        if (valued.Count == 0)
        {
            HashSet<CostStatus> statuses = [];
            foreach (CostBlock block in blocks)
                statuses.Add(block.Status);

            CostStatus status = statuses.Count == 1 ? blocks[0].Status : CostStatus.Partial;
            return new CostTotalResult(true, new CostBlock(basis, status));
        }

        Dictionary<string, double> byModel = new(StringComparer.Ordinal);
        bool fullyPriced = true;
        foreach (CostBlock block in blocks)
        {
            if (block.Status != CostStatus.Priced || block.Value is not double blockValue || !double.IsFinite(blockValue))
                fullyPriced = false;
        }

        double totalValue = 0;
        foreach (CostBlock block in valued)
        {
            if (block.Value is double valuedTotal)
                totalValue += valuedTotal;
            if (block.ByModel is null)
                continue;

            foreach (KeyValuePair<string, double> entry in block.ByModel)
            {
                if (!double.IsFinite(entry.Value))
                    continue;

                byModel[entry.Key] = byModel.GetValueOrDefault(entry.Key) + entry.Value;
            }
        }

        string? currency = currencies.Count == 1 ? currencies.First() : null;
        return new CostTotalResult(
            true,
            new CostBlock(
                basis,
                fullyPriced ? CostStatus.Priced : CostStatus.Partial,
                totalValue,
                currency,
                byModel.Count > 0 ? byModel : null));
    }

    /// <summary>Extracts a structured basis-mismatch verdict when <see cref="SumCosts"/> refused mixed valued bases.</summary>
    public static CostBasisMismatch? CostBasisMismatchOf(IReadOnlyList<CostBlock> blocks, CostTotalResult total)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(total);

        if (total.Ok || total.Problem is null || !string.Equals(total.Problem.Code, CostBasisMismatchCode, StringComparison.Ordinal))
            return null;

        List<CostBlock> valued = [];
        foreach (CostBlock block in blocks)
        {
            if (block.Value is double v && double.IsFinite(v))
                valued.Add(block);
        }

        if (valued.Count == 0)
            return null;

        List<string> bases = [.. blocks.Select(static b => b.Basis.ToWireString()).Distinct(StringComparer.Ordinal).OrderBy(static s => s, StringComparer.Ordinal)];
        Dictionary<string, double?> totalsByBasis = new(StringComparer.Ordinal);
        foreach (string basis in bases)
        {
            double sum = 0;
            int count = 0;
            foreach (CostBlock block in valued)
            {
                if (string.Equals(block.Basis.ToWireString(), basis, StringComparison.Ordinal) &&
                    block.Value is double basisTotal)
                {
                    sum += basisTotal;
                    count++;
                }
            }

            totalsByBasis[basis] = count == 0 ? null : sum;
        }

        return new CostBasisMismatch(
            CostBasisMismatchCode,
            total.Problem.Message,
            bases,
            totalsByBasis);
    }

    /// <summary>
    /// Normalizes raw harness identifiers to canonical front-end IDs while keeping split client surfaces
    /// distinct, matching <c>dash/src/canon/measurability.ts:normalizeHarnessName</c>.
    /// </summary>
    public static string NormalizeHarnessName(string harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        string lower = ToAsciiLower(harness.Trim());
        if (lower is "claude-cli" or "claude-unclassified")
            return lower;
        if (lower == "claude-desktop")
            return "claude-code";
        if (lower is "claude" or "claude-code")
            return lower == "claude" ? "claude-unclassified" : "claude-code";
        if (lower is "copilot-cli" or "copilot-vscode" or "copilot-jetbrains" or "copilot-agent")
            return lower;
        if (lower == "copilot-chat")
            return "copilot-vscode";
        if (lower is "copilot" or "github-copilot")
            return "copilot";
        if (lower is "cursor-agent" or "cursor")
            return "cursor";
        if (lower is "windsurf" or "cascade")
            return "windsurf";
        if (lower is "roo" or "roo-code" or "roo-cline")
            return "roo-code";
        if (lower is "cline-cli" or "cline" or "aider")
            return lower;
        if (lower is "codex-cli" or "codex-desktop" or "codex-unclassified")
            return lower;
        if (lower is "codex" or "openai-codex")
            return lower == "openai-codex" ? "codex-unclassified" : "codex";
        if (lower == "gemini")
            return "gemini";
        if (lower is "antigravity-cli" or "antigravity-ide")
            return lower;
        if (lower is "antigravity" or "agy")
            return "antigravity";
        if (lower == "opencode")
            return "opencode";
        if (lower == "kilo-vscode-legacy")
            return "kilo-vscode-legacy";
        if (lower is "kilo-shared-runtime" or "kilo" or "kilo-code" || lower.StartsWith("kilo", StringComparison.Ordinal))
            return "kilo-shared-runtime";
        if (lower is "kimicode" or "kimi-code")
            return "kimi-code";
        if (lower is "commandcode" or "command-code")
            return "commandcode";
        return lower;
    }

    /// <summary>True when <paramref name="harness"/> is excluded from stored coding-harness rollups (Gemini).</summary>
    public static bool IsExcludedHarnessIdentity(string harness)
    {
        ArgumentNullException.ThrowIfNull(harness);
        return ExcludedHarnessIdentities.Contains(ToAsciiLower(harness.Trim()));
    }

    /// <summary>Returns the normalized canonical coding harness ID, or <see langword="null"/> when excluded.</summary>
    public static string? CanonicalHarnessId(string harness)
    {
        string id = NormalizeHarnessName(harness);
        return IsExcludedHarnessIdentity(id) ? null : id;
    }

    /// <summary>Maps split client surfaces onto their survey family while keeping <c>gemini</c> separate.</summary>
    public static string SurveyFamily(string harness)
    {
        string canonical = NormalizeHarnessName(harness);
        if (string.Equals(canonical, "gemini", StringComparison.Ordinal))
            return "gemini";

        return SurveyFamilyMap.GetValueOrDefault(canonical, canonical);
    }

    /// <summary>Display-level family for a harness ID (collapses split surfaces onto their family).</summary>
    public static string HarnessFamily(string harness) => SurveyFamily(harness);

    /// <summary>
    /// True when the published rate of <paramref name="model"/> changes with each request's prompt
    /// size (GPT-6 Luna and Grok 4.6), so a figure that sums many requests cannot be priced exactly.
    /// </summary>
    public static bool IsPromptTieredModel(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string canonical = ResolveCanonicalModelId(model);
        return string.Equals(canonical, "gpt-6-luna", StringComparison.Ordinal) ||
            string.Equals(canonical, "grok-4.6", StringComparison.Ordinal);
    }

    /// <summary>True for every GitHub Copilot surface (<c>copilot</c>, <c>copilot-cli</c>, <c>copilot-vscode</c>, ...).</summary>
    public static bool IsCopilotHarness(string? harness) =>
        string.Equals(CopilotFamily(harness), "copilot", StringComparison.Ordinal);

    /// <summary>Prices a Copilot turn from <see cref="CopilotCreditsTable"/> while keeping harness-reported costs untouched.</summary>
    public static CostBlock PriceCopilotTurn(
        TokenUsage tokens,
        string? model,
        string? harness,
        CostBlock? existing = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (existing is { Basis: CostBasis.Harness })
            return existing;

        return PriceWithTable(tokens, model, CopilotFamily(harness), CopilotCreditsTable);
    }

    /// <summary>True when <paramref name="harness"/> is billed from the published LiteLLM rate snapshot.</summary>
    public static bool IsPublishedTableHarness(string? harness) =>
        harness is not null && PublishedApplicabilityHarnesses.Contains(ToAsciiLower(harness.Trim()));

    /// <summary>Resolves aliases, router prefixes, and leaf namespaces to a canonical model ID.</summary>
    public static string ResolveCanonicalModelId(string model) =>
        ResolveCanonicalModelIdCore(model, tables: null);

    private static string ResolveCanonicalModelIdCore(string model, PricingTables? tables)
    {
        ArgumentNullException.ThrowIfNull(model);
        string aliased = ResolveAlias(GetCanonicalName(model, tables));
        int slash = aliased.LastIndexOf('/');
        if (slash < 0)
            return aliased;

        string leaf = aliased[(slash + 1)..];
        return leaf.Length == 0 ? aliased : ResolveAlias(GetCanonicalName(leaf, tables));
    }

    /// <summary>True when <paramref name="model"/> is a built-in flat-rate subscription SKU rather than per-token billed.</summary>
    public static bool IsBuiltInFlatRateModel(string model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string leaf = FlatRateLeaf(model);
        if (leaf is "auto" or "auto-genius" or "kimi-for-coding-highspeed")
            return true;

        if (leaf.StartsWith("grok-composer-", StringComparison.Ordinal) ||
            leaf.StartsWith("warp-auto-", StringComparison.Ordinal))
        {
            return true;
        }

        string display = model.Trim();
        return BuiltInFlatRateDisplayRegex().IsMatch(display);
    }

    /// <summary>True when <paramref name="model"/> is a non-empty flat-rate subscription model.</summary>
    public static bool IsFlatRateModel(string? model) =>
        !string.IsNullOrEmpty(model) && IsBuiltInFlatRateModel(model);

    /// <summary>Looks up published per-token pricing for <paramref name="model"/> using TS lookup precedence.</summary>
    public static ModelCosts? GetModelCosts(string model) =>
        GetModelCosts(model, GetTables());

    /// <summary>Looks up per-token pricing for <paramref name="model"/> using explicit <paramref name="tables"/>.</summary>
    public static ModelCosts? GetModelCosts(string model, PricingTables tables)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(tables);
        if (string.IsNullOrWhiteSpace(model))
            return null;

        string withPrefix = DateSuffixRegex().Replace(AtSuffixRegex().Replace(model, string.Empty), string.Empty);
        string canonicalName = GetCanonicalName(model, tables);
        string canonical = ResolveAlias(canonicalName);

        if (!string.Equals(canonical, canonicalName, StringComparison.Ordinal) &&
            string.Equals(withPrefix, canonicalName, StringComparison.Ordinal) &&
            tables.PricingCache.TryGetValue(canonical, out ModelCosts? aliasedPrimary))
        {
            return aliasedPrimary;
        }

        if (tables.PricingCache.TryGetValue(withPrefix, out ModelCosts? byWithPrefix))
            return byWithPrefix;

        if (tables.PricingCache.TryGetValue(canonical, out ModelCosts? byCanonical))
            return byCanonical;

        foreach (string candidate in RoutedModelCandidates(model, tables))
        {
            string aliased = ResolveAlias(candidate);
            if (tables.PricingCache.TryGetValue(aliased, out ModelCosts? byCandidateAlias))
                return byCandidateAlias;
            if (tables.PricingCache.TryGetValue(candidate, out ModelCosts? byCandidate))
                return byCandidate;
        }

        foreach (string key in tables.SortedKeys)
        {
            if (string.Equals(canonical, key, StringComparison.Ordinal) ||
                canonical.StartsWith(key + "-", StringComparison.Ordinal))
            {
                return tables.PricingCache[key];
            }
        }

        string canonicalLower = ToAsciiLower(canonical);
        if (tables.LowercaseIndex.TryGetValue(canonicalLower, out ModelCosts? byLowerCanonical))
            return byLowerCanonical;

        string withPrefixLower = ToAsciiLower(withPrefix);
        if (tables.LowercaseIndex.TryGetValue(withPrefixLower, out ModelCosts? byLowerPrefix))
            return byLowerPrefix;

        string? withPrefixVariant = StripKnownPricingVariantSuffix(withPrefix);
        if (withPrefixVariant is not null && !string.Equals(withPrefixVariant, withPrefix, StringComparison.Ordinal))
        {
            ModelCosts? variantCosts = GetModelCosts(withPrefixVariant, tables);
            if (variantCosts is not null)
                return variantCosts;
        }

        string? canonicalVariant = StripKnownPricingVariantSuffix(canonical);
        if (canonicalVariant is not null &&
            !string.Equals(canonicalVariant, canonical, StringComparison.Ordinal) &&
            !string.Equals(canonicalVariant, withPrefixVariant, StringComparison.Ordinal))
        {
            ModelCosts? variantCosts = GetModelCosts(canonicalVariant, tables);
            if (variantCosts is not null)
                return variantCosts;
        }

        return null;
    }

    /// <summary>Looks up per-token pricing for <paramref name="model"/> via an injected <see cref="IModelPricingLookup"/> or bundled tables.</summary>
    public static ModelCosts? GetModelCosts(string model, IModelPricingLookup? pricing)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (pricing is null)
            return GetModelCosts(model, GetTables());

        if (pricing is PricingTables tables)
            return GetModelCosts(model, tables);

        return string.IsNullOrWhiteSpace(model) ? null : pricing.GetModelCosts(model);
    }

    /// <summary>Computes turn USD cost from published rates, including tiered prompt escalation for GPT-6 Luna and Grok 4.6.</summary>
    public static double CalculateCost(
        string model,
        double inputTokens,
        double outputTokens,
        double cacheCreationTokens,
        double cacheReadTokens,
        double webSearchRequests,
        bool fast = false,
        double oneHourCacheCreationTokens = 0) =>
        CalculateCost(
            model,
            inputTokens,
            outputTokens,
            cacheCreationTokens,
            cacheReadTokens,
            webSearchRequests,
            fast,
            oneHourCacheCreationTokens,
            pricing: null);

    /// <summary>Computes turn USD cost from injected or published rates, including tiered prompt escalation for GPT-6 Luna and Grok 4.6.</summary>
    public static double CalculateCost(
        string model,
        double inputTokens,
        double outputTokens,
        double cacheCreationTokens,
        double cacheReadTokens,
        double webSearchRequests,
        bool fast,
        double oneHourCacheCreationTokens,
        IModelPricingLookup? pricing)
    {
        ArgumentNullException.ThrowIfNull(model);
        ModelCosts? costs = GetModelCosts(model, pricing);
        if (costs is null)
            return 0;

        static double ClampPositive(double value) => double.IsFinite(value) && value > 0 ? value : 0;

        double safeInput = ClampPositive(inputTokens);
        double safeOutput = ClampPositive(outputTokens);
        double safeOneHour = ClampPositive(oneHourCacheCreationTokens);
        double safeCacheCreation = Math.Max(ClampPositive(cacheCreationTokens), safeOneHour);
        double safeFiveMinute = Math.Max(0, safeCacheCreation - safeOneHour);
        double safeCacheRead = ClampPositive(cacheReadTokens);
        double safeWebSearch = ClampPositive(webSearchRequests);
        double promptTokens = safeInput + safeCacheRead;

        ModelCosts tieredCosts = TieredCostsFor(model, costs, promptTokens, safeCacheCreation, pricing as PricingTables);
        double multiplier = fast ? tieredCosts.FastMultiplier : 1.0;

        return multiplier * (
            (safeInput * tieredCosts.InputCostPerToken) +
            (safeOutput * tieredCosts.OutputCostPerToken) +
            (safeFiveMinute * tieredCosts.CacheWriteCostPerToken) +
            (safeOneHour * tieredCosts.CacheWriteCostPerToken * OneHourCacheWriteMultiplier) +
            (safeCacheRead * tieredCosts.CacheReadCostPerToken) +
            (safeWebSearch * tieredCosts.WebSearchCostPerRequest));
    }

    /// <summary>Prices a turn from the bundled published-rate tables under TS applicability scoping rules.</summary>
    public static CostBlock PricePublishedTurn(
        TokenUsage tokens,
        string? model,
        string? harness,
        CostBlock? existing = null) =>
        PricePublishedTurn(tokens, model, harness, pricing: null, existing);

    /// <summary>Prices a turn from injected or bundled published-rate tables under TS applicability scoping rules.</summary>
    public static CostBlock PricePublishedTurn(
        TokenUsage tokens,
        string? model,
        string? harness,
        IModelPricingLookup? pricing,
        CostBlock? existing = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (existing is { Basis: CostBasis.Harness })
            return existing;

        if (!IsPublishedTableHarness(harness))
            return new CostBlock(CostBasis.Published, CostStatus.OutOfScope);

        if (string.IsNullOrEmpty(model))
            return new CostBlock(CostBasis.Published, CostStatus.NoRate);

        if (IsFlatRateModel(model))
            return new CostBlock(CostBasis.Published, CostStatus.NotBilled);

        ModelCosts? costs = GetModelCosts(model, pricing);
        if (costs is null ||
            !(costs.InputCostPerToken > 0 ||
              costs.OutputCostPerToken > 0 ||
              costs.CacheReadCostPerToken > 0 ||
              costs.CacheWriteCostPerToken > 0))
        {
            return new CostBlock(CostBasis.Published, CostStatus.NoRate);
        }

        double billedInput = costs.CacheWriteCostIsExplicit
            ? tokens.FreshInput
            : tokens.FreshInput + tokens.CacheCreation;
        double billedCacheWrite = costs.CacheWriteCostIsExplicit ? tokens.CacheCreation : 0;

        double value = CalculateCost(
            model,
            billedInput,
            tokens.Output,
            billedCacheWrite,
            tokens.CacheRead,
            webSearchRequests: 0,
            fast: false,
            oneHourCacheCreationTokens: 0,
            pricing: pricing);

        return new CostBlock(
            CostBasis.Published,
            CostStatus.Priced,
            value,
            "USD",
            new Dictionary<string, double>(StringComparer.Ordinal) { [model] = value });
    }

    /// <summary>Prices a turn across Copilot credits or API published tables while preserving harness-reported costs.</summary>
    public static CostBlock PriceTurn(
        TokenUsage tokens,
        string? model,
        string? harness,
        CostBlock? existing = null) =>
        PriceTurn(tokens, model, harness, pricing: null, existing);

    /// <summary>Prices a turn across Copilot credits or injected/published tables while preserving harness-reported costs.</summary>
    public static CostBlock PriceTurn(
        TokenUsage tokens,
        string? model,
        string? harness,
        IModelPricingLookup? pricing,
        CostBlock? existing = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (existing is { Basis: CostBasis.Harness })
            return existing;

        return IsCopilotHarness(harness)
            ? PriceCopilotTurn(tokens, model, harness, existing)
            : PricePublishedTurn(tokens, model, harness, pricing, existing);
    }

    /// <summary>
    /// Prices a turn from the token classes that were stored.
    /// A reported input total is the checksum <c>fresh + cache read + cache write</c>.
    /// It is not billed at the fresh rate, and a missing side is not billed as a measured zero.
    /// A reported output total is one rate and is billed when the output class was not stored.
    /// Stored classes that sum to the reported input are priced, with the absent input classes
    /// reconciled to zero. A remainder is priced only for the stored classes and reported as
    /// <see cref="CostStatus.Partial"/>. When no reported input was stored, the input side is
    /// complete only if an input class was stored. An absent output class is not a measured
    /// zero. Reasoning tokens are a subset of output, so a reasoning-only turn is not priced.
    /// A turn with no billable class and no reported total is not priced.
    /// </summary>
    public static CostBlock PriceStoredTokens(
        long? freshInput,
        long? cacheRead,
        long? cacheCreation,
        long? output,
        long? reasoning,
        long? reportedInput,
        long? reportedOutput,
        string? model,
        string? harness,
        IModelPricingLookup? pricing)
    {
        bool canPriceInput = freshInput is not null || cacheRead is not null || cacheCreation is not null;
        bool canPriceOutput = output is not null || reportedOutput is not null;
        bool storedClass = canPriceInput || output is not null;
        bool reportedInputOnly = !canPriceInput && !canPriceOutput && reportedInput is not null;
        if ((!storedClass && reportedInput is null && reportedOutput is null) || reportedInputOnly)
        {
            return new CostBlock(
                CostBasis.Published,
                IsPublishedTableHarness(harness) ? CostStatus.NoRate : CostStatus.OutOfScope);
        }

        long billedFresh = freshInput ?? 0L;
        long billedCacheRead = cacheRead ?? 0L;
        long billedCacheCreation = cacheCreation ?? 0L;
        decimal knownInput = (decimal)billedFresh + billedCacheRead + billedCacheCreation;
        bool inputComplete = IsStoredInputComplete(reportedInput, canPriceInput, knownInput);
        (long billedOutput, bool outputComplete) = ResolveStoredOutput(output, reportedOutput);

        TokenUsage usage = new(
            FreshInput: billedFresh,
            CacheRead: billedCacheRead,
            CacheCreation: billedCacheCreation,
            Output: billedOutput,
            ReportedInput: reportedInput ?? SaturateToInt64(knownInput),
            ReportedOutput: reportedOutput ?? billedOutput,
            Reasoning: reasoning);

        CostBlock priced = PriceTurn(usage, model, harness, pricing, existing: null);
        if (inputComplete && outputComplete)
            return priced;

        return priced.Status == CostStatus.Priced
            ? priced with { Status = CostStatus.Partial }
            : priced;

        static long SaturateToInt64(decimal total)
        {
            if (total > long.MaxValue)
                return long.MaxValue;
            if (total < long.MinValue)
                return long.MinValue;

            return (long)total;
        }
    }

    private static bool IsStoredInputComplete(long? reportedInput, bool canPriceInput, decimal knownInput)
    {
        if (reportedInput is null)
            return canPriceInput;

        return canPriceInput && knownInput == reportedInput.Value;
    }

    private static (long BilledOutput, bool Complete) ResolveStoredOutput(long? output, long? reportedOutput)
    {
        if (output is not null)
            return (output.Value, reportedOutput is null || reportedOutput.Value == output.Value);

        if (reportedOutput is not null)
            return (reportedOutput.Value, true);

        return (0L, false);
    }

    /// <summary>Extracts a validated non-negative finite integer/double measurement from JSON or numeric values.</summary>
    public static long? MeasuredCount(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case long l when l >= 0:
                return l;
            case int i when i >= 0:
                return i;
            case double d when double.IsFinite(d) && d >= 0:
                return (long)d;
            case float f when float.IsFinite(f) && f >= 0:
                return (long)f;
            case decimal m when m >= 0:
                return (long)m;
            case JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double d) && double.IsFinite(d) && d >= 0:
                return (long)d;
            default:
                return null;
        }
    }

    /// <summary>Returns the first non-empty model identifier carried by an <c>llm.invoke</c> record.</summary>
    public static string? ModelIdentityOf(IReadOnlyList<CanonicalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
                continue;

            if (record.Raw is null)
                continue;

            foreach (string key in ModelIdentityKeys)
            {
                if (!record.Raw.TryGetValue(key, out JsonElement value))
                    continue;

                if (value.ValueKind == JsonValueKind.String)
                {
                    string? s = value.GetString();
                    if (!string.IsNullOrEmpty(s))
                        return s;
                }
                else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && double.IsFinite(d))
                {
                    return d.ToString(CultureInfo.InvariantCulture);
                }
            }
        }

        return null;
    }

    /// <summary>True when a context limit provenance source names a placeholder or missing window.</summary>
    public static bool IsUnnamedContextWindow(string? source) =>
        string.Equals(source, "default", StringComparison.Ordinal) ||
        string.Equals(source, "absent", StringComparison.Ordinal);

    /// <summary>
    /// Derives a record group's context window under TS precedence: first usable reported window wins,
    /// else first usable declared window, else <c>absent</c> for pure Antigravity bridge spans, else
    /// <see cref="DefaultContextLimit"/> (<c>default</c>).
    /// </summary>
    public static ContextWindowResult ContextLimitOf(IReadOnlyList<CanonicalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        string? firstDeclared = null;

        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
                continue;

            string? reported = ReportedLimitOf(record);
            if (reported is not null)
            {
                long? limit = UsableLimit(reported);
                return limit.HasValue
                    ? new ContextWindowResult(limit.Value, "reported")
                    : new ContextWindowResult(DefaultContextLimit, "default");
            }

            firstDeclared ??= DeclaredLimitOf(record);
        }

        if (firstDeclared is not null)
        {
            long? limit = UsableLimit(firstDeclared);
            return limit.HasValue
                ? new ContextWindowResult(limit.Value, "declared")
                : new ContextWindowResult(DefaultContextLimit, "default");
        }

        if (AntigravityBridgeGroup(records))
            return new ContextWindowResult(0, "absent");

        return new ContextWindowResult(DefaultContextLimit, "default");
    }

    /// <summary>Derives schema-ranking availability from a record or source measurability declaration.</summary>
    public static AvailabilityResult SchemaRankingAvailability(IReadOnlyDictionary<string, JsonElement>? measurability)
    {
        if (measurability is null)
            return new AvailabilityResult(true, "measured");

        if (TryGetNotMeasurable(measurability, "schema_cost", "This source does not provide schema-cost data.") is { } schemaBlocked)
            return schemaBlocked;

        if (TryGetNotMeasurable(measurability, "tool_definitions", "This source does not provide tool-definition data.") is { } defsBlocked)
            return defsBlocked;

        return new AvailabilityResult(true, "measured");
    }

    /// <summary>Derives context-composition availability from a record or source measurability declaration.</summary>
    public static AvailabilityResult ContextCompositionAvailability(IReadOnlyDictionary<string, JsonElement>? measurability)
    {
        if (measurability is null)
            return new AvailabilityResult(true, "measured");

        foreach (string key in CanonValidation.CanonicalContentKeys)
        {
            if (!measurability.TryGetValue(key, out JsonElement el) || !IsNotMeasurable(el))
                return new AvailabilityResult(true, "measured");
        }

        string firstKey = CanonValidation.CanonicalContentKeys[0];
        if (TryGetNotMeasurable(measurability, firstKey, "This source does not provide message-structure data.") is { } blocked)
            return blocked;

        return new AvailabilityResult(false, "not_measurable", "This source does not provide message-structure data.");
    }

    /// <summary>Detects machine-generated telemetry labels that should not render as human session titles.</summary>
    public static bool IsMachineLabel(string? label)
    {
        if (label is null)
            return false;

        string trimmed = label.Trim();
        if (trimmed.Length == 0)
            return false;

        return SpanOpRegex().IsMatch(trimmed) ||
               ChatModelRegex().IsMatch(trimmed) ||
               UuidExactRegex().IsMatch(trimmed) ||
               SynthPrefixRegex().IsMatch(trimmed) ||
               OpenCodeSpanNameRegex().IsMatch(trimmed) ||
               FactorySpanNameRegex().IsMatch(trimmed) ||
               DroidSynthNameRegex().IsMatch(trimmed) ||
               UnspecifiedModelRegex().IsMatch(trimmed);
    }

    /// <summary>Shortens session identifiers for readable display without stripping scope discriminators.</summary>
    public static string FormatSessionId(string? id)
    {
        string trimmed = (id ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return "Session";

        string rest = SynthPrefixRegex().Replace(trimmed, string.Empty);
        rest = SynthPrefixRegex().Replace(rest, string.Empty);

        int scope = rest.IndexOf('#', StringComparison.Ordinal);
        if (scope > 0)
        {
            string child = rest[(scope + 1)..];
            string shortChild = child.Length > 14 ? child[..14] + "\u2026" : child;
            return $"{FormatSessionId(rest[..scope])}#{shortChild}";
        }

        Match uuid = UuidRegex().Match(rest);
        if (uuid.Success)
        {
            string matched = uuid.Value;
            string prefix = rest[..uuid.Index].TrimEnd(':', '_', '-');
            string shortUuid = $"{matched[..8]}\u2026{matched[^4..]}";
            return prefix.Length > 0 ? $"{prefix} {shortUuid}" : shortUuid;
        }

        return rest.Length <= 18 ? rest : $"{rest[..9]}\u2026{rest[^8..]}";
    }

    /// <summary>Synthesizes an honest display title without fabricating unavailable summaries or agent names.</summary>
    public static string FormatSessionTitle(SessionTitleInput? input)
    {
        if (input is null)
            return "Session";

        string? agent = string.IsNullOrWhiteSpace(input.AgentName) ? null : input.AgentName.Trim();
        string? task = string.IsNullOrWhiteSpace(input.TaskSummary) ? null : input.TaskSummary.Trim();
        string? raw = !string.IsNullOrWhiteSpace(input.RawLabel)
            ? input.RawLabel.Trim()
            : (!string.IsNullOrWhiteSpace(input.Label) ? input.Label.Trim() : null);
        string? humanLabel = raw is not null && !IsMachineLabel(raw) ? raw : null;

        if (agent is not null)
        {
            if (task is not null)
                return $"{agent} \u00B7 {task}";
            if (humanLabel is not null)
                return $"{agent} \u00B7 {humanLabel}";
            return $"{agent} \u00B7 session";
        }

        if (task is not null)
            return task;

        if (humanLabel is not null)
            return humanLabel;

        string? harness = string.IsNullOrWhiteSpace(input.Harness) ? null : input.Harness.Trim();
        if (harness is not null)
            return $"{PrettyHarness(harness)} session";

        string? sessionId = string.IsNullOrWhiteSpace(input.SessionId) ? null : input.SessionId.Trim();
        if (sessionId is not null)
            return FormatSessionId(sessionId);

        return "Session";
    }

    private static string? CopilotFamily(string? harness)
    {
        if (harness is null)
            return null;

        string id = NormalizeHarnessName(harness);
        return id == "copilot" || id.StartsWith("copilot-", StringComparison.Ordinal) ? "copilot" : id;
    }

    private static string PrettyHarness(string raw)
    {
        string cleaned = raw.StartsWith(CanonSynth.FileSourcePrefix, StringComparison.Ordinal)
            ? raw[CanonSynth.FileSourcePrefix.Length..]
            : raw;

        if (string.Equals(ToAsciiLower(cleaned), "opencode", StringComparison.Ordinal))
            return "OpenCode";

        string spaced = cleaned.Replace('-', ' ').Replace('_', ' ');
        return string.Create(spaced.Length, spaced, static (span, src) =>
        {
            bool startOfWord = true;
            for (int i = 0; i < src.Length; i++)
            {
                char ch = src[i];
                if (startOfWord && char.IsLetterOrDigit(ch))
                {
                    span[i] = char.ToUpper(ch, CultureInfo.InvariantCulture);
                    startOfWord = false;
                }
                else
                {
                    span[i] = ch;
                    startOfWord = !char.IsLetterOrDigit(ch) && ch != '_';
                }
            }
        });
    }

    private static string FlatRateLeaf(string model)
    {
        string trimmed = DateSuffixRegex().Replace(AtSuffixRegex().Replace(model.Trim(), string.Empty), string.Empty);
        int slash = trimmed.LastIndexOf('/');
        string leaf = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return ToAsciiLower(leaf);
    }

    private static string ResolveAlias(string model)
    {
        if (BuiltinAliases.TryGetValue(model, out string? mapped))
            return mapped;

        string lowercase = ToAsciiLower(model);
        if (!string.Equals(lowercase, model, StringComparison.Ordinal) &&
            LowercaseBuiltinAliases.TryGetValue(lowercase, out string? lowerMapped))
        {
            return lowerMapped;
        }

        return model;
    }

    private static string GetCanonicalName(string model, PricingTables? tables = null)
    {
        string cleaned = BracketSuffixRegex().Replace(
            DateSuffixRegex().Replace(AtSuffixRegex().Replace(model, string.Empty), string.Empty),
            string.Empty);

        if (BuiltinAliases.ContainsKey(cleaned))
            return cleaned;

        string lowercase = ToAsciiLower(cleaned);
        if (!string.Equals(lowercase, cleaned, StringComparison.Ordinal) &&
            LowercaseBuiltinAliases.ContainsKey(lowercase))
        {
            return cleaned;
        }

        return StripKnownFirstNamespace(cleaned, tables);
    }

    private static string StripKnownFirstNamespace(string model, PricingTables? tables = null)
    {
        int idx = model.IndexOf('/', StringComparison.Ordinal);
        if (idx <= 0)
            return model;

        string head = ToAsciiLower(model[..idx]);
        return (tables ?? GetTables()).KnownNamespaces.Contains(head) ? model[(idx + 1)..] : model;
    }

    private static IReadOnlyList<string> RoutedModelCandidates(string model, PricingTables? tables = null)
    {
        List<string> ids = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Push(string value)
        {
            if (value.Length > 0 && seen.Add(value))
                ids.Add(value);
        }

        Push(model);
        string current = model;
        bool peeled = true;
        while (peeled)
        {
            peeled = false;
            string next = RouterPrefixRegex().Replace(current, string.Empty, 1);
            if (next.Length > 0 && !string.Equals(next, current, StringComparison.Ordinal))
            {
                current = next;
                Push(current);
                peeled = true;
            }
        }

        Push(GetCanonicalName(current, tables));
        return ids;
    }

    private static string? StripKnownPricingVariantSuffix(string model)
    {
        string withoutColon = VariantColonSuffixRegex().Replace(model, string.Empty);
        if (!string.Equals(withoutColon, model, StringComparison.Ordinal))
            return withoutColon;

        string withoutTee = VariantTeeSuffixRegex().Replace(model, string.Empty);
        if (!string.Equals(withoutTee, model, StringComparison.Ordinal))
            return withoutTee;

        return null;
    }

    private static ModelCosts TieredCostsFor(string model, ModelCosts baseCosts, double promptTokens, double cacheCreationTokens, PricingTables? tables = null)
    {
        string canonical = ResolveCanonicalModelIdCore(model, tables);
        if (string.Equals(canonical, "gpt-6-luna", StringComparison.Ordinal) &&
            promptTokens + cacheCreationTokens > Gpt6LunaPromptTokenThreshold)
        {
            return Gpt6LunaHighPromptCosts;
        }

        if (string.Equals(canonical, "grok-4.6", StringComparison.Ordinal) &&
            promptTokens >= Grok46PromptTokenThreshold)
        {
            return Grok46HighPromptCosts;
        }

        return baseCosts;
    }

    private static ModelCosts BuildCosts(
        double input,
        double output,
        double? cacheWrite,
        double? cacheRead,
        double? fast) =>
        new(
            InputCostPerToken: input,
            OutputCostPerToken: output,
            CacheWriteCostPerToken: cacheWrite ?? (input * 1.25),
            CacheReadCostPerToken: cacheRead ?? (input * 0.1),
            WebSearchCostPerRequest: WebSearchCost,
            FastMultiplier: fast ?? 1.0,
            CacheWriteCostIsExplicit: cacheWrite.HasValue);

    private static IReadOnlyDictionary<string, string> BuildLowercaseAliasIndex()
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in BuiltinAliases)
            map.TryAdd(ToAsciiLower(entry.Key), entry.Value);
        return map;
    }

    private static PricingTables LoadEmbeddedPricingTables()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string[] names;
            try
            {
                names = assembly.GetManifestResourceNames();
            }
            catch (NotSupportedException)
            {
                continue;
            }

            string? snapshotName = names.FirstOrDefault(static n => n.EndsWith("litellm-snapshot.json", StringComparison.Ordinal));
            string? fallbackName = names.FirstOrDefault(static n => n.EndsWith("pricing-fallback.json", StringComparison.Ordinal));
            if (snapshotName is not null && fallbackName is not null)
            {
                Dictionary<string, ModelCosts> snapshot = LoadTupleDictionary(assembly, snapshotName);
                Dictionary<string, ModelCosts> fallback = LoadTupleDictionary(assembly, fallbackName);
                return BuildPricingTables(snapshot, fallback);
            }
        }

        return BuildPricingTables(
            new Dictionary<string, ModelCosts>(StringComparer.Ordinal),
            new Dictionary<string, ModelCosts>(StringComparer.Ordinal));
    }

    /// <summary>Builds indexed pricing lookup tables from snapshot and fallback cost maps.</summary>
    public static PricingTables BuildPricingTables(
        IReadOnlyDictionary<string, ModelCosts> snapshotCosts,
        IReadOnlyDictionary<string, ModelCosts> fallbackCosts)
    {
        ArgumentNullException.ThrowIfNull(snapshotCosts);
        ArgumentNullException.ThrowIfNull(fallbackCosts);

        Dictionary<string, ModelCosts> pricingCache = new(snapshotCosts, StringComparer.Ordinal);
        Dictionary<string, ModelCosts> fallbackMap = new(fallbackCosts, StringComparer.Ordinal);

        pricingCache["composer-2.5"] = BuildCosts(0.5e-6, 2.5e-6, 0.5e-6, 0.2e-6, null);
        pricingCache["composer-2"] = BuildCosts(0.5e-6, 2.5e-6, 0.5e-6, 0.2e-6, null);
        pricingCache["composer-1.5"] = BuildCosts(3.5e-6, 17.5e-6, 3.5e-6, 0.35e-6, null);
        pricingCache["composer-1"] = BuildCosts(1.25e-6, 10e-6, 1.25e-6, 0.125e-6, null);

        List<string> sortedKeys = [.. pricingCache.Keys];
        sortedKeys.Sort(static (a, b) =>
        {
            int cmp = b.Length.CompareTo(a.Length);
            return cmp != 0 ? cmp : string.CompareOrdinal(a, b);
        });

        Dictionary<string, ModelCosts> lowercaseIndex = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ModelCosts> entry in pricingCache)
        {
            if (entry.Value.InputCostPerToken > 0 || entry.Value.OutputCostPerToken > 0)
                lowercaseIndex.TryAdd(ToAsciiLower(entry.Key), entry.Value);
        }

        foreach (KeyValuePair<string, ModelCosts> entry in fallbackMap)
        {
            if (entry.Value.InputCostPerToken > 0 || entry.Value.OutputCostPerToken > 0)
                lowercaseIndex.TryAdd(ToAsciiLower(entry.Key), entry.Value);
        }

        HashSet<string> knownNamespaces = new(ExtraNamespaces, StringComparer.Ordinal);
        foreach (string key in pricingCache.Keys.Concat(fallbackMap.Keys))
        {
            int idx = key.IndexOf('/', StringComparison.Ordinal);
            if (idx > 0)
                knownNamespaces.Add(ToAsciiLower(key[..idx]));
        }

        knownNamespaces.Remove("ollama");
        return new PricingTables(pricingCache, sortedKeys, lowercaseIndex, knownNamespaces);
    }

    private static Dictionary<string, ModelCosts> LoadTupleDictionary(Assembly assembly, string resourceName)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded pricing resource '{resourceName}'.");
        using JsonDocument doc = JsonDocument.Parse(stream);
        Dictionary<string, ModelCosts> map = new(StringComparer.Ordinal);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array || prop.Value.GetArrayLength() < 2)
                continue;

            double input = prop.Value[0].GetDouble();
            double output = prop.Value[1].GetDouble();
            double? cacheWrite = prop.Value.GetArrayLength() > 2 && prop.Value[2].ValueKind == JsonValueKind.Number
                ? prop.Value[2].GetDouble()
                : null;
            double? cacheRead = prop.Value.GetArrayLength() > 3 && prop.Value[3].ValueKind == JsonValueKind.Number
                ? prop.Value[3].GetDouble()
                : null;
            double? fast = prop.Value.GetArrayLength() > 4 && prop.Value[4].ValueKind == JsonValueKind.Number
                ? prop.Value[4].GetDouble()
                : null;

            map[prop.Name] = BuildCosts(input, output, cacheWrite, cacheRead, fast);
        }

        return map;
    }

    private static string? ReportedLimitOf(CanonicalRecord record) =>
        ReadNumericOrStringAttribute(record.Raw, ContextLimitKeys);

    private static string? DeclaredLimitOf(CanonicalRecord record) =>
        ReadNumericOrStringAttribute(record.Raw, [DeclaredContextLimitKey]);

    private static string? ReadNumericOrStringAttribute(
        IReadOnlyDictionary<string, JsonElement>? raw,
        IReadOnlyList<string> keys)
    {
        if (raw is null)
            return null;

        foreach (string key in keys)
        {
            if (!raw.TryGetValue(key, out JsonElement value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                string? s = value.GetString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }
            else if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double d) && double.IsFinite(d))
            {
                return d.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    private static long? UsableLimit(string named)
    {
        if (double.TryParse(named, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d) &&
            double.IsFinite(d) && d > 0)
        {
            return (long)d;
        }

        return null;
    }

    private static bool IsAntigravityBridgeSpan(CanonicalRecord record) =>
        record.Raw is not null &&
        record.Raw.TryGetValue("gen_ai.agent.name", out JsonElement agent) &&
        agent.ValueKind == JsonValueKind.String &&
        string.Equals(agent.GetString(), "antigravity", StringComparison.Ordinal);

    private static bool AntigravityBridgeGroup(IReadOnlyList<CanonicalRecord> records)
    {
        bool sawInvoke = false;
        foreach (CanonicalRecord record in records)
        {
            if (!string.Equals(record.Op, "llm.invoke", StringComparison.Ordinal))
                continue;

            sawInvoke = true;
            if (!IsAntigravityBridgeSpan(record))
                return false;
        }

        return sawInvoke;
    }

    private static bool IsNotMeasurable(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return string.Equals(el.GetString(), "not_measurable", StringComparison.Ordinal);

        if (el.ValueKind == JsonValueKind.Object &&
            el.TryGetProperty("availability", out JsonElement avail) &&
            avail.ValueKind == JsonValueKind.String)
        {
            return string.Equals(avail.GetString(), "not_measurable", StringComparison.Ordinal);
        }

        return false;
    }

    private static AvailabilityResult? TryGetNotMeasurable(
        IReadOnlyDictionary<string, JsonElement> measurability,
        string key,
        string defaultReason)
    {
        if (measurability.TryGetValue(key, out JsonElement el) && IsNotMeasurable(el))
        {
            string reason = el.ValueKind == JsonValueKind.Object &&
                            el.TryGetProperty("reason", out JsonElement r) &&
                            r.ValueKind == JsonValueKind.String &&
                            r.GetString() is { Length: > 0 } reportedReason
                ? reportedReason
                : defaultReason;
            return new AvailabilityResult(false, "not_measurable", reason);
        }

        return null;
    }

}

/// <summary>Looks up per-token model pricing rates without exposing underlying cache tables.</summary>
public interface IModelPricingLookup
{
    /// <summary>Looks up per-token pricing rates for <paramref name="model"/>.</summary>
    /// <param name="model">Model identifier to look up.</param>
    ModelCosts? GetModelCosts(string model);
}

/// <summary>Indexed pricing lookup tables constructed from snapshot and fallback cost dictionaries.</summary>
public record PricingTables(
    IReadOnlyDictionary<string, ModelCosts> PricingCache,
    IReadOnlyList<string> SortedKeys,
    IReadOnlyDictionary<string, ModelCosts> LowercaseIndex,
    IReadOnlySet<string> KnownNamespaces) : IModelPricingLookup
{
    /// <summary>Looks up per-token pricing for <paramref name="model"/> using these tables.</summary>
    /// <param name="model">Model identifier to look up.</param>
    protected virtual ModelCosts? GetModelCosts(string model) => CanonPricing.GetModelCosts(model, this);

    /// <inheritdoc />
    ModelCosts? IModelPricingLookup.GetModelCosts(string model) => GetModelCosts(model);
}
