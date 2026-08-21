using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Docs.Graph;
using KyberWeave.Core.Docs.Model;

namespace KyberWeave.Core.Docs.Analysis.Glossary;

/// <summary>Projects approved managed-glossary knowledge into DocGraph.</summary>
public sealed class ManagedGlossaryGraphContributor : IDocGraphContributor
{
    private readonly IReadOnlyList<TermSnapshot> _terms;

    public ManagedGlossaryGraphContributor(ManagedGlossaryLoadResult glossary)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        _terms = SnapshotApproved(glossary.Terms);
        ValidateGraphIdentities(_terms);
    }

    public DocGraphContribution Contribute(DocumentSet documents, ICodeGraphResolver codeGraph)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(codeGraph);

        List<DocGraphNode> nodes = new List<DocGraphNode>();
        List<DocGraphEdge> edges = new List<DocGraphEdge>();
        HashSet<string> emittedNodeIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (TermSnapshot term in _terms.OrderBy(item => item.Term, StringComparer.Ordinal))
        {
            string termId = TermId(term.Term);
            AddTermNode(nodes, emittedNodeIds, termId, term.Term);
            foreach (SenseSnapshot sense in term.Senses.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                string senseId = $"sense:{sense.Id}";
                AddNode(nodes, emittedNodeIds, new DocGraphNode(
                    senseId,
                    "Sense",
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["term"] = term.Term,
                        ["definition"] = sense.Definition
                    }));
                edges.Add(new DocGraphEdge("HAS_SENSE", termId, senseId));

                foreach (string alias in sense.Aliases
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(value => value, StringComparer.Ordinal))
                {
                    string aliasId = TermId(alias);
                    AddTermNode(nodes, emittedNodeIds, aliasId, alias);
                    if (!StringComparer.Ordinal.Equals(aliasId, termId))
                        edges.Add(new DocGraphEdge("ALIAS_OF", aliasId, senseId));
                }

                foreach (string scope in sense.Scopes
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(value => value, StringComparer.Ordinal))
                {
                    AddScopeEdges(nodes, emittedNodeIds, edges, senseId, scope, codeGraph);
                }

                foreach (string evidenceId in sense.EvidenceIds
                             .Distinct(StringComparer.Ordinal)
                             .OrderBy(value => value, StringComparer.Ordinal))
                {
                    AddEvidenceNode(nodes, emittedNodeIds, evidenceId);
                    edges.Add(new DocGraphEdge("EVIDENCED_BY", senseId, evidenceId));
                }
            }
        }

        return new DocGraphContribution(nodes, edges.Distinct().ToArray());
    }

    private static IReadOnlyList<TermSnapshot> SnapshotApproved(
        IReadOnlyList<GlossaryLookupResult> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return terms
            .Select(term => new TermSnapshot(
                term.Term,
                term.Senses
                    .Where(sense => sense.Status == GlossarySenseStatus.Approved)
                    .Select(sense => new SenseSnapshot(
                        sense.Id,
                        sense.Definition,
                        sense.Scopes.ToArray(),
                        sense.Aliases.ToArray(),
                        sense.EvidenceIds.ToArray()))
                    .ToArray()))
            .Where(term => term.Senses.Count > 0)
            .ToArray();
    }

    private static void ValidateGraphIdentities(IReadOnlyList<TermSnapshot> terms)
    {
        Dictionary<string, string> termIdentities = new Dictionary<string, string>(StringComparer.Ordinal);
        HashSet<string> senseIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (TermSnapshot term in terms)
        {
            RegisterTermIdentity(termIdentities, term.Term);
            foreach (SenseSnapshot sense in term.Senses)
            {
                string senseId = $"sense:{sense.Id}";
                if (!senseIds.Add(senseId))
                {
                    throw new InvalidDataException(
                        $"Managed glossary graph identity collision for sense id '{senseId}'.");
                }

                foreach (string alias in sense.Aliases)
                    RegisterTermIdentity(termIdentities, alias);
            }
        }
    }

    private static void RegisterTermIdentity(
        IDictionary<string, string> identities,
        string term)
    {
        string id = TermId(term);
        string semanticIdentity = term.Trim().ToLowerInvariant();
        if (identities.TryGetValue(id, out string? existing)
            && !StringComparer.Ordinal.Equals(existing, semanticIdentity))
        {
            throw new InvalidDataException(
                $"Managed glossary graph identity collision for term id '{id}'.");
        }

        identities[id] = semanticIdentity;
    }

    private static void AddScopeEdges(
        ICollection<DocGraphNode> nodes,
        ISet<string> emittedNodeIds,
        ICollection<DocGraphEdge> edges,
        string senseId,
        string scope,
        ICodeGraphResolver codeGraph)
    {
        const string componentPrefix = "component:";
        const string codePrefix = "code-ref:";
        if (scope.StartsWith(componentPrefix, StringComparison.Ordinal))
        {
            string component = scope[componentPrefix.Length..];
            if (component.Length > 0)
            {
                string componentId = componentPrefix + component;
                AddNode(nodes, emittedNodeIds, new DocGraphNode(
                    componentId,
                    "Component",
                    new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = component }));
                edges.Add(new DocGraphEdge("SCOPED_TO", senseId, componentId));
            }
            return;
        }

        if (!scope.StartsWith(codePrefix, StringComparison.Ordinal)) return;
        string symbol = scope[codePrefix.Length..];
        foreach (CodeGraphNode node in codeGraph.ResolveSymbol(symbol).OrderBy(node => node.Id, StringComparer.Ordinal))
            edges.Add(new DocGraphEdge("SCOPED_TO", senseId, node.Id));
    }

    private static void AddEvidenceNode(
        ICollection<DocGraphNode> nodes,
        ISet<string> emittedNodeIds,
        string evidenceId)
    {
        if (string.IsNullOrWhiteSpace(evidenceId)) return;
        AddNode(nodes, emittedNodeIds, new DocGraphNode(
            evidenceId,
            "Claim",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["id"] = evidenceId }));
    }

    private static void AddTermNode(
        ICollection<DocGraphNode> nodes,
        ISet<string> emittedNodeIds,
        string id,
        string name) =>
        AddNode(nodes, emittedNodeIds, new DocGraphNode(
            id,
            "Term",
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = name }));

    private static void AddNode(
        ICollection<DocGraphNode> nodes,
        ISet<string> emittedNodeIds,
        DocGraphNode node)
    {
        if (emittedNodeIds.Add(node.Id)) nodes.Add(node);
    }

    private static string TermId(string term)
    {
        string slug = new string(term.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray());
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return $"term:{slug}";
    }

    private sealed record TermSnapshot(string Term, IReadOnlyList<SenseSnapshot> Senses);

    private sealed record SenseSnapshot(
        string Id,
        string Definition,
        IReadOnlyList<string> Scopes,
        IReadOnlyList<string> Aliases,
        IReadOnlyList<string> EvidenceIds);
}
