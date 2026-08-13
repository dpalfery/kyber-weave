using System.Collections.ObjectModel;

namespace KyberWeave.Core.Docs.Graph;

/// <summary>One node in the immutable documentation graph projection.</summary>
public sealed record DocGraphNode
{
    /// <summary>Creates a node and defensively snapshots its properties.</summary>
    public DocGraphNode(
        string id,
        string label,
        IReadOnlyDictionary<string, string?> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(properties);

        Id = id;
        Label = label;
        Properties = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(properties, StringComparer.Ordinal));
    }

    public string Id { get; }

    public string Label { get; }

    public IReadOnlyDictionary<string, string?> Properties { get; }
}
