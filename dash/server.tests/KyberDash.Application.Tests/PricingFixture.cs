using System.Runtime.CompilerServices;
using System.Text.Json;
using KyberDash.Application.Repositories;
using KyberDash.Domain;

namespace KyberDash.Application.Tests;

/// <summary>
/// Loads the published LiteLLM snapshot and the curated fallback rates shipped with the data layer
/// and installs them as the process-wide pricing provider.
/// </summary>
/// <remarks>
/// Domain tests never touch SQLite, so they cannot use the data layer's repository. They read the two
/// JSON files from the repository instead and hand the parsed rates to
/// <see cref="CanonPricing.ConfigurePricingProvider"/>, which is exactly what happens at runtime — just
/// with the fixture read from disk rather than an embedded resource.
/// </remarks>
public static class PricingFixture
{
    private static readonly object Gate = new();

    internal static PricingTables Tables { get; private set; } = CanonPricing.BuildPricingTables(
        new Dictionary<string, ModelCosts>(StringComparer.Ordinal),
        new Dictionary<string, ModelCosts>(StringComparer.Ordinal));

    /// <summary>Installs the fixture once per test process, before any test class is constructed.</summary>
    [ModuleInitializer]
    internal static void Initialize() => EnsureInstalled();

    /// <summary>Installs the pricing fixture for the current call.</summary>
    /// <remarks>
    /// <see cref="CanonPricing.ConfigurePricingProvider"/> clears the cached tables it hands out, so a
    /// caller that runs a lookup between two installs can observe an empty cache. Installing at the top
    /// of every test that asserts prices removes that ordering dependency.
    /// </remarks>
    public static void EnsureInstalled()
    {
        lock (Gate)
        {
            string directory = PricingDirectory();
            Dictionary<string, ModelCosts> snapshot = LoadTupleDictionary(Path.Combine(directory, "litellm-snapshot.json"));
            Dictionary<string, ModelCosts> fallback = LoadTupleDictionary(Path.Combine(directory, "pricing-fallback.json"));
            PricingTables tables = CanonPricing.BuildPricingTables(snapshot, fallback);
            Tables = tables;

            CanonPricing.ConfigurePricingProvider(() => Tables);
        }
    }

    /// <summary>Absolute path of the pricing fixture directory.</summary>
    /// <returns>The repository's pricing folder.</returns>
    public static string PricingDirectory()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            string[] candidates =
            [
                Path.Combine(current, "server", "KyberDash.Data", "Pricing"),
                Path.Combine(current, "dash", "server", "KyberDash.Data", "Pricing")
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(Path.Combine(candidate, "litellm-snapshot.json")))
                {
                    return candidate;
                }
            }

            // A gate builds into .agent_scratchpad/<task>/artifacts, whose ancestors never include dash/.
            // The repository root above it does, so accept the dash/ layout as well.
            string dashCandidate = Path.Combine(current, "dash", "server", "KyberDash.Data", "Pricing", "litellm-snapshot.json");
            if (File.Exists(dashCandidate))
            {
                return Path.Combine(current, "dash", "server", "KyberDash.Data", "Pricing");
            }

            current = Path.GetDirectoryName(current);
        }

        throw new DirectoryNotFoundException(
            "Could not locate dash/server/KyberDash.Data/Pricing from the test binary location.");
    }

    private static Dictionary<string, ModelCosts> LoadTupleDictionary(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, ModelCosts> map = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            JsonElement tuple = property.Value;
            if (tuple.ValueKind != JsonValueKind.Array || tuple.GetArrayLength() < 2)
            {
                continue;
            }

            double input = tuple[0].GetDouble();
            double output = tuple[1].GetDouble();
            double? cacheWrite = OptionalNumber(tuple, 2);
            double? cacheRead = OptionalNumber(tuple, 3);
            double? fast = OptionalNumber(tuple, 4);

            map[property.Name] = new ModelCosts(
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
