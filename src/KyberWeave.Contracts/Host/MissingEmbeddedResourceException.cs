namespace KyberWeave.Contracts.Host;

/// <summary>An embedded resource was asked for by a name the assembly does not contain.</summary>
public sealed class MissingEmbeddedResourceException : InvalidOperationException
{
    /// <summary>Creates the exception with no resource detail.</summary>
    public MissingEmbeddedResourceException()
    {
    }

    /// <summary>Creates the exception with the message given.</summary>
    /// <param name="message">The message.</param>
    public MissingEmbeddedResourceException(string? message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the message and inner exception given.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public MissingEmbeddedResourceException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a resource that is not there.</summary>
    /// <param name="name">The manifest resource name that was asked for.</param>
    /// <param name="assemblyName">The assembly that was searched.</param>
    public MissingEmbeddedResourceException(string name, string assemblyName)
        : base($"The embedded resource '{name}' is missing from the assembly '{assemblyName}'.")
    {
        ResourceName = name;
    }

    /// <summary>Gets the manifest resource name that was asked for.</summary>
    public string ResourceName { get; } = string.Empty;
}
