using KyberWeave.Core.CodeGraph;

namespace KyberWeave.Tests;

/// <summary>Deterministic <see cref="ICodeGraphResolver"/> for CodeGraph port contract tests.</summary>
internal sealed class FakeCodeGraphResolver : ICodeGraphResolver
{
    private readonly Dictionary<string, List<CodeGraphNode>> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeGraphNode> _routes = new(StringComparer.Ordinal);
    private readonly List<string> _files = [];

    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public string DatabasePath => ":memory:";

    public static FakeCodeGraphResolver WithSymbols(params (string Name, CodeGraphNode Node)[] symbols)
    {
        var fake = new FakeCodeGraphResolver();
        foreach (var (name, node) in symbols)
        {
            fake._symbols[name] = [node];
            if (!string.IsNullOrWhiteSpace(node.FilePath))
                fake._files.Add(node.FilePath);
        }

        return fake;
    }

    public static FakeCodeGraphResolver WithRoutes(params string[] routes)
    {
        var fake = new FakeCodeGraphResolver();
        foreach (var route in routes)
            fake._routes[route] = new CodeGraphNode($"route-{route}", "route", route, route, string.Empty, "csharp", 0);

        return fake;
    }

    public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name) =>
        _symbols.TryGetValue(name, out var nodes) ? nodes : [];

    public IReadOnlyList<CodeGraphNode> ResolveRoute(string route) =>
        _routes.TryGetValue(route, out var node) ? [node] : [];

    public bool HasFilesUnder(string relativePathPrefix)
    {
        var normalized = relativePathPrefix.Replace('\\', '/').TrimEnd('/');
        return _files.Exists(p => p.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> CandidateNames(string like)
    {
        var prefix = like.Length <= 4 ? like : like[..4];
        return _symbols.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public IReadOnlyList<string> AllRoutes() => _routes.Keys.ToList();
}
