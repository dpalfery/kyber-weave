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
    public static string ReadEffective(string homeDirectory, ICollection<string> warnings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentNullException.ThrowIfNull(warnings);

        string path = Path.Combine(homeDirectory, SquadResponseGuidanceDeployer.FileName);

        try
        {
            if (!File.Exists(path))
                return DefaultText;

            string homeText = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(homeText))
                return DefaultText;

            return homeText;
        }
        catch (Exception exception) when (SquadResponseGuidanceDeployer.IsDegradableFailure(exception))
        {
            warnings.Add($"Failed to read response guidance at '{path}'; using the shipped default instead.");
            return DefaultText;
        }
    }

    private static string LoadDefaultText()
    {
        Assembly assembly = typeof(SquadResponseGuidanceDeployer).Assembly;
        string resourceName = "Squad.response-guidance.md";
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded response-guidance resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
