namespace KyberWeave.Contracts.Host;

/// <summary>The rule a lease name must follow before an operating system is asked to carry it.</summary>
/// <remarks>
/// A named mutex on Windows treats a leading <c>Global\</c> or <c>Local\</c> as a namespace and a backslash as its
/// separator, while the same name on another platform means something else. A lease name is therefore a plain name:
/// both implementations validate it with <see cref="Validate"/> so they refuse the same input.
/// </remarks>
public static class LeaseNames
{
    /// <summary>The longest lease name accepted, well below the shortest operating-system limit.</summary>
    public const int MaximumLength = 200;

    /// <summary>Checks a lease name and returns it unchanged.</summary>
    /// <param name="name">The lease name to check.</param>
    /// <returns>The name, when it is usable.</returns>
    /// <exception cref="ArgumentException">The name is null, empty or whitespace, longer than <see cref="MaximumLength"/>, or contains a namespace prefix or path separator.</exception>
    public static string Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"A lease name is at most {MaximumLength} characters; got {name.Length}.",
                nameof(name));
        }

        if (name.Contains('\\', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A lease name is a plain name, without a namespace prefix or a path separator; got '{name}'.",
                nameof(name));
        }

        return name;
    }
}
