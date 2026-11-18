using System.Reflection;
using System.Text;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>The response-preferences default shipped inside the assembly.</summary>
public static class SquadResponseGuidanceSource
{
    private static readonly Lazy<string> DefaultTextLazy = new(LoadDefaultText);

    /// <summary>The shipped default text embedded in the assembly.</summary>
    public static string DefaultText => DefaultTextLazy.Value;

    /// <summary>The manifest resource the shipped default is read from.</summary>
    public const string ResourceName = "Squad.response-guidance.md";

    /// <summary>Opens the embedded default, or returns null when the assembly does not carry it.</summary>
    public static Stream? OpenDefaultResource() =>
        typeof(SquadResponseGuidanceSource).Assembly.GetManifestResourceStream(ResourceName);

    private static string LoadDefaultText()
    {
        string resourceName = ResourceName;
        using Stream stream = OpenDefaultResource()
                              ?? throw new SquadShippedGuidanceUnavailableException(
                                  $"The embedded response-guidance resource '{resourceName}' is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
