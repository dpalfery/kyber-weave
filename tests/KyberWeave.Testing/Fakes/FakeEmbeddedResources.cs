using System.Text;
using KyberWeave.Contracts.Host;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IEmbeddedResources"/>: the resources a test added, and nothing else.</summary>
public sealed class FakeEmbeddedResources : IEmbeddedResources
{
    private readonly Dictionary<string, byte[]> _resources = new(StringComparer.Ordinal);

    /// <summary>Adds a resource whose text is UTF-8 encoded.</summary>
    /// <param name="name">The manifest resource name.</param>
    /// <param name="text">The resource text.</param>
    /// <returns>This fake, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A resource with that name is already added.</exception>
    public FakeEmbeddedResources Add(string name, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(text);
        return Add(name, Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Adds a resource with the bytes given.</summary>
    /// <param name="name">The manifest resource name.</param>
    /// <param name="content">The resource bytes.</param>
    /// <returns>This fake, for chaining.</returns>
    /// <exception cref="InvalidOperationException">A resource with that name is already added.</exception>
    public FakeEmbeddedResources Add(string name, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        if (!_resources.TryAdd(name, content))
        {
            throw new InvalidOperationException($"The resource '{name}' is already added.");
        }

        return this;
    }

    /// <inheritdoc />
    public Stream OpenRead(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _resources.TryGetValue(name, out byte[]? content)
            ? new MemoryStream(content, writable: false)
            : throw new MissingEmbeddedResourceException(name, nameof(FakeEmbeddedResources));
    }

    /// <inheritdoc />
    public string ReadAllText(string name)
    {
        using Stream stream = OpenRead(name);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
