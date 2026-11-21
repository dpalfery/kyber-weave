using KyberDash.Data.Collector.Sources;

namespace KyberDash.Application;

/// <summary>
/// The one harness-keyed mapper lookup shared by the refresh and ingestion engines: the
/// mapper's raw harness id (later registrations overwrite earlier ones) plus its normalized
/// fold, resolved raw-first so an exact registration always wins.
/// </summary>
public static class HarnessMapperLookup
{
    /// <summary>Builds the harness-keyed mapper lookup the engines resolve mappers through.</summary>
    /// <param name="mappers">Registered per-harness mappers.</param>
    /// <returns>Lookup keyed by raw and normalized harness id.</returns>
    public static Dictionary<string, IHarnessMapper> Build(IEnumerable<IHarnessMapper>? mappers)
    {
        Dictionary<string, IHarnessMapper> lookup = new(StringComparer.Ordinal);
        if (mappers is null)
        {
            return lookup;
        }

        foreach (IHarnessMapper mapper in mappers)
        {
            ArgumentNullException.ThrowIfNull(mapper);
            lookup[mapper.HarnessId] = mapper;
            string normalized = HarnessSourceRegistry.NormalizeHarnessId(mapper.HarnessId);
            _ = lookup.TryAdd(normalized, mapper);
        }

        return lookup;
    }

    /// <summary>Resolves the mapper for a harness id, falling back to its normalized fold.</summary>
    /// <param name="lookup">Lookup built by <see cref="Build"/>.</param>
    /// <param name="harnessId">Harness id to resolve.</param>
    /// <returns>The registered mapper, or <see langword="null"/> when none resolves.</returns>
    public static IHarnessMapper? Resolve(IReadOnlyDictionary<string, IHarnessMapper> lookup, string harnessId)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        if (lookup.TryGetValue(harnessId, out IHarnessMapper? direct))
        {
            return direct;
        }

        string normalized = HarnessSourceRegistry.NormalizeHarnessId(harnessId);
        return lookup.GetValueOrDefault(normalized);
    }
}
