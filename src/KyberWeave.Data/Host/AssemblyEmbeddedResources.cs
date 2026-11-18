using System.Reflection;
using System.Text;
using KyberWeave.Contracts.Host;

namespace KyberWeave.Data.Host;

/// <summary>The resources compiled into one assembly, read by their manifest name.</summary>
/// <remarks>
/// The assembly is a constructor argument because the product embeds each template in the assembly that owns it, and a
/// caller that shipped its resource elsewhere reads it by naming that assembly.
/// </remarks>
public sealed class AssemblyEmbeddedResources : IEmbeddedResources
{
    private readonly Assembly _assembly;

    /// <summary>Creates the seam over <paramref name="assembly"/>.</summary>
    /// <param name="assembly">The assembly whose embedded resources are read.</param>
    public AssemblyEmbeddedResources(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        _assembly = assembly;
    }

    /// <inheritdoc />
    public Stream OpenRead(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _assembly.GetManifestResourceStream(name)
            ?? throw new MissingEmbeddedResourceException(name, _assembly.GetName().Name ?? _assembly.FullName!);
    }

    /// <inheritdoc />
    public string ReadAllText(string name)
    {
        using Stream stream = OpenRead(name);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
