using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KyberWeave.Core.Squad.Briefs;

/// <summary>
/// A condition on the run's facts: every clause must hold. A clause on an unknown fact holds, so a fact
/// the machine could not resolve never removes instructions.
/// </summary>
public sealed class BriefCondition
{
    /// <summary>The condition with no clauses.</summary>
    public static BriefCondition Always { get; } = new([]);

    private readonly IReadOnlyList<(string Fact, IReadOnlyList<string> Values)> _clauses;

    private BriefCondition(IReadOnlyList<(string Fact, IReadOnlyList<string> Values)> clauses)
    {
        _clauses = clauses;
    }

    /// <summary>The condition as the map spells it.</summary>
    public string Text => _clauses.Count == 0
        ? "always"
        : string.Join("; ", _clauses.Select(clause => $"{clause.Fact}={string.Join('|', clause.Values)}"));

    /// <summary>Parses <c>always</c> or <c>fact=a|b; fact=c</c>.</summary>
    /// <exception cref="BriefMapException">The text names a fact or value the vocabulary does not have.</exception>
    public static BriefCondition Parse(string text, string where)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, "always", StringComparison.Ordinal))
        {
            return Always;
        }

        List<(string, IReadOnlyList<string>)> clauses = [];
        foreach (string clause in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = clause.IndexOf('=', StringComparison.Ordinal);
            string fact = equals < 0 ? clause : clause[..equals].Trim();
            if (equals < 0 || !BriefFacts.Vocabulary.TryGetValue(fact, out IReadOnlyList<string>? known))
            {
                throw new BriefMapException(
                    $"{where}: '{clause}' is not 'fact=value'. Facts: {string.Join(", ", BriefFacts.Names)}.");
            }

            string[] values = clause[(equals + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? unknown = values.FirstOrDefault(value => !known.Contains(value, StringComparer.Ordinal));
            if (values.Length == 0 || unknown is not null)
            {
                throw new BriefMapException(
                    $"{where}: '{unknown ?? clause}' is not a value of '{fact}'. Values: {string.Join(", ", known)}.");
            }

            clauses.Add((fact, values));
        }

        return new BriefCondition(clauses);
    }

    /// <summary>True when every clause holds for <paramref name="facts"/>.</summary>
    public bool Holds(BriefFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        foreach ((string fact, IReadOnlyList<string> values) in _clauses)
        {
            string? actual = facts.Get(fact);
            if (actual is not null && !values.Contains(actual, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One reference file in the map: its condition and the condition of each heading in it.</summary>
/// <param name="File">The authored file name, for example <c>plan-path.md</c>.</param>
/// <param name="When">The condition on the whole file.</param>
/// <param name="Sections">Heading to extra condition, keyed by the heading as the map spells it.</param>
public sealed record BriefFileRule(string File, BriefCondition When, IReadOnlyDictionary<string, BriefCondition> Sections);

/// <summary>The map from each agent's reference files and headings to the facts they apply to.</summary>
public sealed class BriefMap
{
    /// <summary>The schema value the map carries.</summary>
    public const string Schema = "kyber-squad.briefs/v1";

    private const string ResourceName = "Squad.briefs.yml";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly IReadOnlyDictionary<string, IReadOnlyList<BriefFileRule>> _agents;

    private BriefMap(IReadOnlyDictionary<string, IReadOnlyList<BriefFileRule>> agents)
    {
        _agents = agents;
    }

    /// <summary>The agents the map describes, in map order.</summary>
    public IReadOnlyCollection<string> Agents => _agents.Keys.ToArray();

    /// <summary>The map shipped inside this assembly.</summary>
    public static BriefMap Load()
    {
        Assembly assembly = typeof(BriefMap).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new BriefMapException($"The shipped brief map '{ResourceName}' is missing from {assembly.GetName().Name}.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses a map from its YAML text.</summary>
    /// <exception cref="BriefMapException">The text is not a valid map.</exception>
    public static BriefMap Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        MapDocument document;
        try
        {
            document = Yaml.Deserialize<MapDocument>(yaml) ?? new MapDocument();
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new BriefMapException($"The brief map is not valid YAML: {exception.Message}");
        }

        if (!string.Equals(document.Schema, Schema, StringComparison.Ordinal))
        {
            throw new BriefMapException($"The brief map's schema is '{document.Schema}', expected '{Schema}'.");
        }

        Dictionary<string, IReadOnlyList<BriefFileRule>> agents = new(StringComparer.Ordinal);
        foreach ((string agent, List<FileDocument> files) in document.Agents)
        {
            List<BriefFileRule> rules = [];
            foreach (FileDocument file in files)
            {
                if (string.IsNullOrWhiteSpace(file.File))
                {
                    throw new BriefMapException($"Agent '{agent}' has a file entry with no 'file'.");
                }

                string where = $"{agent}/{file.File}";
                Dictionary<string, BriefCondition> sections = new(StringComparer.Ordinal);
                foreach ((string heading, string when) in file.Sections)
                {
                    sections[heading] = BriefCondition.Parse(when, $"{where} § {heading}");
                }

                rules.Add(new BriefFileRule(file.File, BriefCondition.Parse(file.When ?? "always", where), sections));
            }

            agents[agent] = rules;
        }

        return new BriefMap(agents);
    }

    /// <summary>The file rules of <paramref name="agent"/> in map order, or null when the map does not know the agent.</summary>
    public IReadOnlyList<BriefFileRule>? FilesOf(string agent) =>
        _agents.TryGetValue(agent, out IReadOnlyList<BriefFileRule>? files) ? files : null;

    private sealed class MapDocument
    {
        public string? Schema { get; set; }

        public Dictionary<string, List<FileDocument>> Agents { get; set; } = [];
    }

    private sealed class FileDocument
    {
        public string? File { get; set; }

        public string? When { get; set; }

        public Dictionary<string, string> Sections { get; set; } = [];
    }
}

/// <summary>The brief map is malformed, or a brief was asked for something it does not describe.</summary>
public sealed class BriefMapException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public BriefMapException()
    {
    }

    /// <summary>Creates the exception with the message that names the problem.</summary>
    public BriefMapException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the message and the failure that caused it.</summary>
    public BriefMapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
