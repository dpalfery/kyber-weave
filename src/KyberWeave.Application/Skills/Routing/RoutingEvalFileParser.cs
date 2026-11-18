using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Skills.Routing;

/// <summary>Raised when a routing eval file's YAML cannot be read.</summary>
/// <remarks>
/// The caller that reads the file reports it: the CLI prints the message and exits as refused
/// input, so a malformed eval file is a diagnostic rather than a stack trace from the YAML reader.
/// </remarks>
public sealed class RoutingEvalParseException : Exception
{
    /// <summary>Required by <see cref="Exception"/> serialization and by CA1032.</summary>
    public RoutingEvalParseException()
    {
    }

    public RoutingEvalParseException(string message)
        : base(message)
    {
    }

    public RoutingEvalParseException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}

/// <summary>Parses a routing eval set from its YAML text.</summary>
public static class RoutingEvalFileParser
{
    /// <summary>Parses a routing eval set from its YAML text.</summary>
    /// <param name="yaml">The eval file's text, read by the caller.</param>
    /// <returns>The parsed set, empty when the text holds no cases.</returns>
    /// <exception cref="RoutingEvalParseException">The text is not a YAML document this parser can read.</exception>
    public static RoutingEvalFile Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        try
        {
            return deserializer.Deserialize<RoutingEvalFile?>(yaml) ?? new RoutingEvalFile();
        }
        catch (YamlException error)
        {
            throw new RoutingEvalParseException($"The routing eval file is not valid YAML: {error.Message}", error);
        }
    }
}
