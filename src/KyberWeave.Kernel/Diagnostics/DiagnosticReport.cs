namespace KyberWeave.Core.Diagnostics;

/// <summary>Aggregated diagnostics for one command run, with convenience roll-ups.</summary>
public sealed class DiagnosticReport
{
    private readonly List<Diagnostic> _items = new();
    private readonly OrderedDictionary<string, object?> _metrics = new(StringComparer.Ordinal);

    public IReadOnlyList<Diagnostic> Items => _items;

    /// <summary>
    /// Scalar measurements produced by the command, in the order they were added.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Metrics => _metrics;

    public void Add(Diagnostic d) => _items.Add(d);
    public void AddRange(IEnumerable<Diagnostic> ds) => _items.AddRange(ds);

    /// <summary>Adds or replaces a scalar command metric without changing its display order.</summary>
    public void AddMetric(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!IsScalar(value))
        {
            throw new ArgumentException("Diagnostic metrics must be JSON scalar values.", nameof(value));
        }

        _metrics[key] = value;
    }

    public int Count(Severity s) => _items.Count(i => i.Severity == s);

    public bool HasErrors => _items.Any(i => i.Severity is Severity.Error or Severity.Critical);
    public bool HasCritical => _items.Any(i => i.Severity == Severity.Critical);

    public int Errors => Count(Severity.Error) + Count(Severity.Critical);
    public int Warnings => Count(Severity.Warning);
    public int Infos => Count(Severity.Info);

    private static bool IsScalar(object? value) => value switch
    {
        float number => float.IsFinite(number),
        double number => double.IsFinite(number),
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or decimal => true,
        _ => false
    };
}
