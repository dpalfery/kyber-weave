using System.Reflection;
using System.Text;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
/// Resolves the effective response-preferences text for a user session, prioritising the
/// operator's home file and otherwise falling back to the shipped default.
/// </summary>
public static partial class SquadResponseGuidanceSource
{
    private static readonly Lazy<string> DefaultTextLazy = new(LoadDefaultText);

    /// <summary>The shipped default text embedded in the assembly.</summary>
    public static string DefaultText => DefaultTextLazy.Value;

    /// <summary>Returns the effective advice text for the current user home.</summary>
    /// <remarks>
    ///     Throws the deployer's degradable <c>SquadShippedGuidanceUnavailableException</c> (an
    ///     <see cref="IOException"/>) when the build shipped without its default, so callers that already
    ///     degrade on I/O failures handle it without a new path.
    /// </remarks>
    public static string ReadEffective(string homeDirectory, ICollection<string> warnings)
        => ReadEffective(homeDirectory, warnings, readDefault: null);

    /// <summary>
    ///     Resolves the effective text, reading the shipped default through <paramref name="readDefault"/>
    ///     when given. Injected so a test can drive a build that shipped without its default.
    /// </summary>
    internal static string ReadEffective(string homeDirectory, ICollection<string> warnings, Func<string>? readDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentNullException.ThrowIfNull(warnings);

        string path = Path.Combine(homeDirectory, SquadResponseGuidanceDeployer.FileName);

        // One read, no existence probe: a probe-then-read leaves a window where the file can change or
        // vanish, and File.Exists answers false for a directory, which must be reported rather than skipped.
        string homeText;
        try
        {
            homeText = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ShippedDefault(readDefault);
        }
        catch (Exception exception) when (SquadResponseGuidanceDeployer.IsDegradableFailure(exception))
        {
            warnings.Add($"Failed to read response guidance at '{path}'; using the shipped default instead.");
            return ShippedDefault(readDefault);
        }

        if (!string.IsNullOrWhiteSpace(homeText))
        {
            return homeText;
        }

        // A present-but-blank file is operator input the run is ignoring; say so once instead of falling back silently.
        warnings.Add($"Response guidance at '{path}' is blank; using the shipped default instead.");
        return ShippedDefault(readDefault);
    }

    private static string ShippedDefault(Func<string>? readDefault)
        => readDefault is null ? DefaultText : readDefault();

    private static string LoadDefaultText()
    {
        Assembly assembly = typeof(SquadResponseGuidanceDeployer).Assembly;
        string resourceName = "Squad.response-guidance.md";
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
                              ?? throw new SquadShippedGuidanceUnavailableException(
                                  $"The embedded response-guidance resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
