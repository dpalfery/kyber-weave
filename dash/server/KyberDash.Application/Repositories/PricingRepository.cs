using System.Globalization;
using System.Reflection;
using System.Text.Json;
using KyberDash.Contracts.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application.Repositories;

/// <summary>
/// Model rate-table repository loading the embedded LiteLLM snapshot and fallback tables and
/// publishing them through <see cref="CanonPricing.ConfigurePricingProvider"/>.
/// </summary>
/// <remarks>
/// The rate tables are embedded in the data assembly, so this repository only locates that assembly
/// and hands it to the loader; it names no data-layer type, keeping the application layer free of
/// concrete data-layer dependencies. Tables are read once and cached for the process lifetime.
/// </remarks>
public class PricingRepository : IPricingRepository
{
    /// <summary>Embedded resource suffix of the LiteLLM published rate snapshot.</summary>
    public const string SnapshotResourceSuffix = "litellm-snapshot.json";

    /// <summary>Embedded resource suffix of the curated fallback rates.</summary>
    public const string FallbackResourceSuffix = "pricing-fallback.json";

    /// <summary>Name of the data assembly that carries the embedded rate tables.</summary>
    public const string DataAssemblyName = "KyberDash.Data";

    private static readonly Lock Gate = new();
    private static PricingTables? cached;

    /// <summary>Loads or returns cached published and fallback model rate tables.</summary>
    /// <returns>Published plus fallback pricing tables.</returns>
    public PricingTables LoadPricingTables()
    {
        lock (Gate)
        {
            if (cached is not null)
            {
                return cached;
            }

            Assembly dataAssembly = ResolveDataAssembly();
            Dictionary<string, ModelCosts> snapshot = LoadTupleDictionary(dataAssembly, SnapshotResourceSuffix);
            Dictionary<string, ModelCosts> fallback = LoadTupleDictionary(dataAssembly, FallbackResourceSuffix);
            cached = CanonPricing.BuildPricingTables(snapshot, fallback);
            return cached;
        }
    }

    /// <summary>Drops the cached tables so the next call reloads them from the embedded resources.</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            cached = null;
        }
    }

    /// <summary>Returns catalog refresh status metadata describing the loaded rate tables.</summary>
    /// <returns>Model catalog snapshot payload.</returns>
    public JsonElement GetModelCatalogSnapshot()
    {
        PricingTables tables = LoadPricingTables();
        return JsonSerializer.SerializeToElement(new
        {
            source = "embedded_snapshot",
            models = tables.SortedKeys.Count,
        });
    }

    private static Assembly ResolveDataAssembly()
    {
        try
        {
            return Assembly.Load(new AssemblyName(DataAssemblyName));
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {DataAssemblyName} assembly carrying the embedded pricing tables could not be loaded."),
                exception);
        }
    }

    private static Dictionary<string, ModelCosts> LoadTupleDictionary(Assembly assembly, string fileName)
    {
        string? match = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.Ordinal));
        Dictionary<string, ModelCosts> map = new(StringComparer.Ordinal);
        if (match is null)
        {
            return map;
        }

        using Stream stream = assembly.GetManifestResourceStream(match)
            ?? throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Missing embedded pricing resource '{match}'."));
        using JsonDocument doc = JsonDocument.Parse(stream);
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
