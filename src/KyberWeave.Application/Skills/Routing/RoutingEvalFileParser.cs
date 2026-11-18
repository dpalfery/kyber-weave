using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Skills.Routing;

/// <summary>Parses a routing eval set from its YAML text.</summary>
public static class RoutingEvalFileParser
{
    /// <summary>Parses a routing eval set from its YAML text.</summary>
    /// <param name="yaml">The eval file's text, read by the caller.</param>
    /// <returns>The parsed set, empty when the text holds no cases.</returns>
    public static RoutingEvalFile Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<RoutingEvalFile?>(yaml) ?? new RoutingEvalFile();
    }
}
