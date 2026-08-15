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

    public bool Equals(DocGraphNode? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null)
        {
            return false;
        }

        if (!string.Equals(Id, other.Id, StringComparison.Ordinal) ||
            !string.Equals(Label, other.Label, StringComparison.Ordinal) ||
            Properties.Count != other.Properties.Count)
        {
            return false;
        }

        foreach (var (key, value) in Properties)
        {
            if (!other.Properties.TryGetValue(key, out var otherValue) ||
                !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(Label, StringComparer.Ordinal);
        foreach (var (key, value) in Properties.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            hash.Add(key, StringComparer.Ordinal);
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
