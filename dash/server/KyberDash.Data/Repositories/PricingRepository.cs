using System.Reflection;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
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

    private readonly ICanonStoreRepository? store;
    private readonly Lock gate = new();
    private PricingTables? tables;

    /// <summary>
    /// Initializes a repository that reports catalog status from <paramref name="store"/> and registers
    /// itself as the process-wide pricing provider.
    /// </summary>
    /// <param name="store">Canonical store read for catalog refresh status, or <see langword="null"/> to report the embedded snapshot only.</param>
    public PricingRepository(ICanonStoreRepository? store = null)
    {
        this.store = store;
        if (store is not null)
            CanonPricing.ConfigurePricingProvider(LoadPricingTables);
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
        if (store is { } canonical)
        {
            try
            {
                status = canonical.GetMetadata(CatalogStatusMetadataKey) ?? status;
                lastRefreshAt = canonical.GetMetadata(CatalogStatusMetadataKey + ".last_refresh_at");
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                // Catalog status is advisory; a store that cannot answer must not fail a read.
                status = "unknown";
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
                continue;

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
