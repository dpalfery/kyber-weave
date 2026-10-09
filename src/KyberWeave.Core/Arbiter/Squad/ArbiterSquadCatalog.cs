using System.Reflection;
using System.Text;
using KyberWeave.Core.Parsing;

namespace KyberWeave.Core.Arbiter;

/// <summary>One Squad agent as the Arbiter sees it: routing facts, not instructions.</summary>
/// <param name="Name">The agent name.</param>
/// <param name="Description">The canonical description (roster option text).</param>
/// <param name="DelegatesTo">Agents this agent may delegate to.</param>
/// <param name="CapabilityProfile">The capability profile name.</param>
/// <param name="TargetClass">The target class derived from the profile.</param>
public sealed record ArbiterSquadAgent(
    string Name,
    string Description,
    IReadOnlyList<string> DelegatesTo,
    string CapabilityProfile,
    string TargetClass);

/// <summary>One review lens as the Arbiter sees it: its Applicability section.</summary>
/// <param name="Name">The lens name.</param>
/// <param name="Applicability">The Applicability section body.</param>
public sealed record ArbiterSquadLens(string Name, string Applicability);

/// <summary>
/// The embedded Squad catalog: every agent's <c>delegates-to</c>, description and
/// capability profile with its target class, and every lens's Applicability section.
/// The rules read it inside a host repository that has no canonical tree.
/// </summary>
/// <remarks>
/// Agent and lens files are embedded at build time so that single-file distributions
/// can route and gate without depending on runtime filesystem paths. The embedded
/// copies equal the canonical <c>products/kyber-squad</c> tree; the catalog test
/// loads that tree through <c>SquadSourceLoader</c> and asserts equality.
/// </remarks>
public sealed class ArbiterSquadCatalog
{
    private const string AgentResourcePrefix = "Arbiter.Roster.";
    private const string LensResourcePrefix = "Arbiter.Lens.";
    private const string MarkdownSuffix = ".md";

    private static readonly Lazy<ArbiterSquadCatalog> Embedded =
        new(LoadEmbeddedUncached, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Embedded agents by name.</summary>
    public IReadOnlyDictionary<string, ArbiterSquadAgent> Agents { get; }

    /// <summary>Embedded lenses by name.</summary>
    public IReadOnlyDictionary<string, ArbiterSquadLens> Lenses { get; }

    public ArbiterSquadCatalog(
        IReadOnlyDictionary<string, ArbiterSquadAgent> agents,
        IReadOnlyDictionary<string, ArbiterSquadLens> lenses)
    {
        Agents = agents;
        Lenses = lenses;
    }

    /// <summary>Loads the embedded catalog (cached).</summary>
    public static ArbiterSquadCatalog LoadEmbedded() => Embedded.Value;

    /// <summary>Derives the target class from a capability profile name.</summary>
    /// <remarks>
    /// The classes are fixed: implementation (worker, publishing-worker,
    /// documentation), planner (architect, product-planning), read-only
    /// (investigator, read-only, reviewer). Any other profile — including the
    /// orchestrator's — is not Squad routable and maps to <c>not-squad</c>.
    /// </remarks>
    public static string TargetClassFor(string capabilityProfile)
    {
        ArgumentNullException.ThrowIfNull(capabilityProfile);
        return capabilityProfile switch
        {
            "worker" or "publishing-worker" or "documentation" => "implementation",
            "architect" or "product-planning" => "planner",
            "investigator" or "read-only" or "reviewer" => "read-only",
            _ => "not-squad",
        };
    }

    /// <summary>
    /// Loads a catalog from directories on disk: top-level <c>*.md</c> agents and a
    /// lenses directory, mirroring how the canonical source loader discovers them.
    /// </summary>
    public static ArbiterSquadCatalog LoadFromDirectories(string agentsDirectory, string lensesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(lensesDirectory);
        Dictionary<string, ArbiterSquadAgent> agents = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(agentsDirectory, "*.md", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            ArbiterSquadAgent agent = ParseAgent(
                Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
            agents[agent.Name] = agent;
        }

        Dictionary<string, ArbiterSquadLens> lenses = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(lensesDirectory, "*.md", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            lenses[name] = new ArbiterSquadLens(name, ReadApplicabilitySection(File.ReadAllText(path)));
        }

        return new ArbiterSquadCatalog(agents, lenses);
    }

    /// <summary>Extracts the <c>## Applicability</c> section body from lens markdown.</summary>
    internal static string ReadApplicabilitySection(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        List<string> collected = [];
        bool inSection = false;

        foreach (string line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSection)
                    break;
                inSection = string.Equals(line.Trim(), "## Applicability", StringComparison.Ordinal);
                continue;
            }

            if (inSection)
                collected.Add(line);
        }

        return string.Join("\n", collected).Trim();
    }

    private static ArbiterSquadCatalog LoadEmbeddedUncached()
    {
        Assembly assembly = typeof(ArbiterSquadCatalog).Assembly;
        string[] names = assembly.GetManifestResourceNames();
        Dictionary<string, ArbiterSquadAgent> agents = new(StringComparer.Ordinal);
        foreach (string resource in names
                     .Where(name => name.StartsWith(AgentResourcePrefix, StringComparison.Ordinal) &&
                         name.EndsWith(MarkdownSuffix, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            string name = resource[AgentResourcePrefix.Length..^MarkdownSuffix.Length];
            agents[name] = ParseAgent(name, ReadResource(assembly, resource));
        }

        Dictionary<string, ArbiterSquadLens> lenses = new(StringComparer.Ordinal);
        foreach (string resource in names
                     .Where(name => name.StartsWith(LensResourcePrefix, StringComparison.Ordinal) &&
                         name.EndsWith(MarkdownSuffix, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            string name = resource[LensResourcePrefix.Length..^MarkdownSuffix.Length];
            lenses[name] = new ArbiterSquadLens(name, ReadApplicabilitySection(ReadResource(assembly, resource)));
        }

        return new ArbiterSquadCatalog(agents, lenses);
    }

    private static string ReadResource(Assembly assembly, string resource)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{resource}' is missing from {assembly.GetName().Name}.");
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static ArbiterSquadAgent ParseAgent(string fileName, string markdown)
    {
        FrontmatterReadResult frontmatter = MarkdownFrontmatterReader.Read(markdown);
        if (!frontmatter.HasFrontmatter)
        {
            throw new InvalidOperationException(
                $"Embedded agent '{fileName}' has no YAML frontmatter.");
        }

        ArbiterAgentFrontmatter? parsed;
        try
        {
            parsed = MarkdownFrontmatterReader.Deserializer.Deserialize<ArbiterAgentFrontmatter>(frontmatter.Yaml);
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new InvalidOperationException(
                $"Embedded agent '{fileName}' frontmatter is not valid: {exception.Message}",
                exception);
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Name))
        {
            throw new InvalidOperationException(
                $"Embedded agent '{fileName}' declares no name.");
        }

        return new ArbiterSquadAgent(
            parsed.Name!,
            parsed.Description ?? string.Empty,
            parsed.DelegatesTo is null ? [] : [.. parsed.DelegatesTo],
            parsed.CapabilityProfile ?? string.Empty,
            TargetClassFor(parsed.CapabilityProfile ?? string.Empty));
    }

    /// <summary>The agent frontmatter fields the Arbiter reads. Everything else is ignored.</summary>
    internal sealed class ArbiterAgentFrontmatter
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? CapabilityProfile { get; set; }

        public List<string>? DelegatesTo { get; set; }
    }
}
