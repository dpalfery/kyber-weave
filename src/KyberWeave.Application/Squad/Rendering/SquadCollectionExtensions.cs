namespace KyberWeave.Core.Squad.Rendering;

/// <summary>Bulk add for the collections a render context hands to its renderers.</summary>
internal static class SquadCollectionExtensions
{
    public static void AddRange<T>(this ICollection<T> collection, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(items);

        foreach (T item in items)
        {
            collection.Add(item);
        }
    }
}
