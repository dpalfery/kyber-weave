using System.Reflection;

namespace KyberWeave.Tests.Fixtures;

/// <summary>The Arbiter hook payloads compiled into this assembly, so a unit test reads them from memory.</summary>
internal static class ArbiterHookFixtures
{
    /// <summary>Reads one Factory payload or transcript fixture.</summary>
    /// <param name="name">The file name, such as <c>pre-task-dispatch.json</c>.</param>
    internal static string Factory(string name)
    {
        using Stream stream = typeof(ArbiterHookFixtures).Assembly.GetManifestResourceStream("arbiter-hooks/factory/" + name)
            ?? throw new FileNotFoundException($"The embedded Factory fixture '{name}' does not exist.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
