namespace KyberWeave.Core.Diagnostics;

/// <summary>Aggregated diagnostics for one command run, with convenience roll-ups.</summary>
public sealed class DiagnosticReport
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;

    public void Add(Diagnostic d) => _items.Add(d);
    public void AddRange(IEnumerable<Diagnostic> ds) => _items.AddRange(ds);

    public int Count(Severity s) => _items.Count(i => i.Severity == s);

    public bool HasErrors => _items.Any(i => i.Severity is Severity.Error or Severity.Critical);
    public bool HasCritical => _items.Any(i => i.Severity == Severity.Critical);

    public int Errors => Count(Severity.Error) + Count(Severity.Critical);
    public int Warnings => Count(Severity.Warning);
    public int Infos => Count(Severity.Info);
}
