using System.Globalization;
using System.Reflection;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Domain;

namespace KyberDash.Data.Repositories;

/// <summary>
/// Loads the bundled model rate tables (<c>litellm-snapshot.json</c> plus
/// <c>pricing-fallback.json</c>) and exposes them to <see cref="CanonPricing"/>, so cost accounting
/// never re-reads embedded resources per call.
/// </summary>
public class PricingRepository : IPricingRepository
{
    /// <summary>Embedded resource name of the LiteLLM published rate snapshot.</summary>
    public const string SnapshotResourceName = "KyberDash.Data.Pricing.litellm-snapshot.json";

    /// <summary>Embedded resource name of the curated fallback rates.</summary>
    public const string FallbackResourceName = "KyberDash.Data.Pricing.pricing-fallback.json";

    /// <summary>Metadata key holding the last successful model-catalog refresh outcome.</summary>
    public const string CatalogStatusMetadataKey = "model_context_window_catalog.status";

    /// <summary>Value of the catalog <c>source</c> field: rates come from the embedded snapshot.</summary>
    public const string EmbeddedSnapshotSource = "embedded_snapshot";

    private readonly IOperationalStore? store;
    private readonly Lock gate = new();
    private PricingTables? tables;

    /// <summary>
    /// Initializes a repository that reports catalog status from <paramref name="store"/> and registers
    /// itself as the process-wide pricing provider.
    /// </summary>
    /// <param name="store">Operational store read for catalog refresh status, or <see langword="null"/> to report the embedded snapshot only.</param>
    public PricingRepository(IOperationalStore? store = null)
    {
        this.store = store;
        if (store is not null)
        {
            CanonPricing.ConfigurePricingProvider(LoadPricingTables);
        }
    }

    /// <inheritdoc />
    public PricingTables LoadPricingTables()
    {
        lock (gate)
        {
            return tables ??= Load();
        }
    }

    /// <inheritdoc />
    public JsonElement GetModelCatalogSnapshot()
    {
        string status = "embedded";
        string? lastRefreshAt = null;
        if (store is { } operational)
        {
            try
            {
                StoredModelCatalogSnapshot catalog = operational.GetModelContextWindowCatalog();
                status = string.IsNullOrWhiteSpace(catalog.Status) || string.Equals(catalog.Status, "empty", StringComparison.Ordinal)
                    ? "embedded"
                    : catalog.Status;
                lastRefreshAt = LatestVendorRefreshAtUtc(catalog.Vendors);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or SqliteExecutionException or CircuitBreakerOpenException)
            {
                // Catalog status is advisory; a store that cannot answer must not fail a read.
                status = "unknown";
                lastRefreshAt = null;
            }
        }

        return JsonSerializer.SerializeToElement(new
        {
            source = EmbeddedSnapshotSource,
            status,
            lastRefreshAt,
            updatedAt = (string?)null,
            models = Array.Empty<object>(),
        });
    }

    private static string? LatestVendorRefreshAtUtc(IReadOnlyList<StoredModelCatalogVendorStatus>? vendors)
    {
        if (vendors is null || vendors.Count == 0)
        {
            return null;
        }

        string? best = null;
        bool bestParsed = false;
        DateTimeOffset bestUtc = default;

        foreach (StoredModelCatalogVendorStatus vendor in vendors)
        {
            if (vendor is null || string.IsNullOrWhiteSpace(vendor.LastRefreshAtUtc))
            {
                continue;
            }

            string candidate = vendor.LastRefreshAtUtc;
            bool candidateParsed = DateTimeOffset.TryParse(
                candidate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset candidateUtc);

            if (best is null || IsLaterRefreshTimestamp(candidate, candidateParsed, candidateUtc, best, bestParsed, bestUtc))
            {
                best = candidate;
                bestParsed = candidateParsed;
                bestUtc = candidateUtc;
            }
        }

        return best;
    }

    private static bool IsLaterRefreshTimestamp(
        string candidate,
        bool candidateParsed,
        DateTimeOffset candidateUtc,
        string best,
        bool bestParsed,
        DateTimeOffset bestUtc)
    {
        if (candidateParsed && bestParsed)
        {
            int comparison = candidateUtc.CompareTo(bestUtc);
            return comparison > 0 || (comparison == 0 && StringComparer.Ordinal.Compare(candidate, best) > 0);
        }

        if (candidateParsed != bestParsed)
        {
            return candidateParsed;
        }

        return StringComparer.Ordinal.Compare(candidate, best) > 0;
    }

    /// <summary>Reads both embedded resource streams and builds the indexed tables exactly once.</summary>
    private static PricingTables Load()
    {
        Assembly assembly = typeof(PricingRepository).Assembly;
        Dictionary<string, ModelCosts> snapshot = LoadTupleDictionary(assembly, SnapshotResourceName);
        Dictionary<string, ModelCosts> fallback = LoadTupleDictionary(assembly, FallbackResourceName);
        return CanonPricing.BuildPricingTables(snapshot, fallback);
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
            {
                continue;
            }

            double input = prop.Value[0].GetDouble();
            double output = prop.Value[1].GetDouble();
            double? cacheWrite = OptionalNumber(prop.Value, 2);
            double? cacheRead = OptionalNumber(prop.Value, 3);
            double? fast = OptionalNumber(prop.Value, 4);

            map[prop.Name] = new ModelCosts(
                InputCostPerToken: input,
                OutputCostPerToken: output,
                CacheWriteCostPerToken: cacheWrite ?? (input * 1.25),
                CacheReadCostPerToken: cacheRead ?? (input * 0.1),
                WebSearchCostPerRequest: CanonPricing.WebSearchCost,
                FastMultiplier: fast ?? 1.0,
                CacheWriteCostIsExplicit: cacheWrite.HasValue);
        }

        return map;
    }

    private static double? OptionalNumber(JsonElement tuple, int index) =>
        tuple.GetArrayLength() > index && tuple[index].ValueKind == JsonValueKind.Number
            ? tuple[index].GetDouble()
            : null;
}
