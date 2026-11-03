using System.Text;
using KyberWeave.Core.Docs.Scaffolding;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies the five embedded gate catalogue family files (<c>Gates.&lt;family&gt;.yml</c>) that the
/// review council and the developer task gates are rendered from.
/// </summary>
/// <remarks>
/// The catalogue is executable configuration. A missing source URL leaves a gate that cannot be traced
/// back to the vendor documentation it was checked against, and a string where an argv list belongs
/// is a shell command line the runner must never receive. The files are read by manifest name, the way
/// the released binary reads them, so an absent or misnamed resource fails here rather than at render time.
/// </remarks>
public sealed class GateCatalogueResourceTests
{
    /// <summary>Path fragments that name this repository's own layout, which no written gate may contain.</summary>
    private static readonly string[] RepositoryLayoutLiterals = ["KyberWeave.sln", "dash/", "src-tauri/Cargo.toml"];

    /// <summary>The argv-carrying keys of a gate entry.</summary>
    private static readonly string[] ArgvKeys = ["run", "fix"];

    /// <summary>
    /// Each family file is an embedded resource that parses as YAML and declares <c>schema: 1</c>.
    /// Without this, <c>GateCatalogue</c> refuses the file at load time and every gate in the family is lost.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("typescript")]
    [InlineData("rust")]
    [InlineData("go")]
    [InlineData("python")]
    public void FamilyFileIsEmbeddedAndDeclaresSchemaOne(string family)
    {
        YamlMappingNode root = LoadFamily(family);

        Assert.Equal("1", Scalar(root, "schema"));
    }

    /// <summary>
    /// Every gate id, council or task, has a <c>sources:</c> entry that is an https URL, so each gate can be
    /// traced to the vendor documentation it was verified against. A shared id needs one entry only.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("typescript")]
    [InlineData("rust")]
    [InlineData("go")]
    [InlineData("python")]
    public void EveryGateIdHasAnHttpsSourceUrl(string family)
    {
        YamlMappingNode root = LoadFamily(family);
        YamlMappingNode sources = Assert.IsType<YamlMappingNode>(Child(root, "sources"));
        List<string> offenders = [];

        foreach (CatalogueGate gate in EnumerateGates(root))
        {
            string? url = Scalar(sources, gate.Id);
            if (url is null)
            {
                offenders.Add($"{Label(gate)} has no sources entry");
            }
            else if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                offenders.Add($"{Label(gate)} source '{url}' is not an https URL");
            }
        }

        Assert.True(offenders.Count == 0, $"{family} catalogue: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// No family file names this repository's own layout (<c>KyberWeave.sln</c>, <c>dash/</c>,
    /// <c>src-tauri/Cargo.toml</c>). A gate that hard-codes one of those paths runs in this repository and
    /// fails in every consuming repository, because the catalogue is rendered there.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("typescript")]
    [InlineData("rust")]
    [InlineData("go")]
    [InlineData("python")]
    public void NoFamilyFileContainsThisRepositoryLayout(string family)
    {
        string text = ReadFamilyText(family);
        List<string> found = RepositoryLayoutLiterals
            .Where(literal => text.Contains(literal, StringComparison.Ordinal))
            .ToList();

        Assert.True(found.Count == 0, $"{family} catalogue names this repository's layout: {string.Join(", ", found)}");
    }

    /// <summary>
    /// Every <c>run</c> and <c>fix</c> is a YAML sequence of argv elements, never a string. A string would be
    /// handed to a shell, which the catalogue forbids (R1.4). Every gate must also declare a <c>run</c>.
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("typescript")]
    [InlineData("rust")]
    [InlineData("go")]
    [InlineData("python")]
    public void EveryRunAndFixIsAnArgvSequence(string family)
    {
        YamlMappingNode root = LoadFamily(family);
        List<string> offenders = [];

        foreach (CatalogueGate gate in EnumerateGates(root))
        {
            if (Child(gate.Node, "run") is not YamlSequenceNode)
            {
                offenders.Add($"{Label(gate)} run is missing or not a sequence");
            }

            YamlNode? fix = Child(gate.Node, "fix");
            if (fix is not null && fix is not YamlSequenceNode)
            {
                offenders.Add($"{Label(gate)} fix is not a sequence");
            }
        }

        Assert.True(offenders.Count == 0, $"{family} catalogue: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// Every gate whose program is <c>npm</c> or <c>npx</c> declares <c>os</c>. Those programs are <c>.cmd</c>
    /// shims on Windows and do not start reliably without a shell, so the gate must name the platforms it runs
    /// on (R1.7).
    /// </summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("typescript")]
    [InlineData("rust")]
    [InlineData("go")]
    [InlineData("python")]
    public void EveryNpmOrNpxGateDeclaresOs(string family)
    {
        YamlMappingNode root = LoadFamily(family);
        List<string> offenders = [];

        foreach (CatalogueGate gate in EnumerateGates(root))
        {
            if (RunsNodeTool(gate.Node) && Child(gate.Node, "os") is null)
            {
                offenders.Add($"{Label(gate)} runs npm or npx without os");
            }
        }

        Assert.True(offenders.Count == 0, $"{family} catalogue: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// The council gates of each family cover a format check, a lint check, a type check or build, and a test
    /// run, wherever the technology's toolchain provides one (R1.3). Rust has no separate build or typecheck
    /// gate, so <c>clippy</c> stands for the type check: it runs the compiler's checking pass. Optional gates
    /// (written commented out until the operator uncomments them) do not count toward coverage.
    /// </summary>
    [Theory]
    [InlineData("csharp", "build")]
    [InlineData("typescript", "typecheck")]
    [InlineData("rust", "clippy")]
    [InlineData("go", "build")]
    [InlineData("python", "typecheck")]
    public void CouncilGatesCoverFormatLintTypeCheckAndTest(string family, string typeCheckId)
    {
        YamlMappingNode root = LoadFamily(family);
        List<CatalogueGate> council = EnumerateGates(root)
            .Where(gate => gate.Section == "gates" && !IsOptional(gate.Node))
            .ToList();
        List<string> missing = [];

        if (!council.Any(gate => Scalar(gate.Node, "role") == "format"))
        {
            missing.Add("role format");
        }

        if (!council.Any(gate => Scalar(gate.Node, "role") == "lint"))
        {
            missing.Add("role lint");
        }

        if (!council.Any(gate => gate.Id == typeCheckId))
        {
            missing.Add($"type check '{typeCheckId}'");
        }

        if (!council.Any(gate => gate.Id == "test"))
        {
            missing.Add("test");
        }

        Assert.True(missing.Count == 0, $"{family} council gates are missing: {string.Join(", ", missing)}");
    }

    private sealed record CatalogueGate(string Section, string Id, YamlMappingNode Node);

    private static string ReadFamilyText(string family)
    {
        string resourceName = $"Gates.{family}.yml";
        using Stream? stream = typeof(KyberStandardsTemplates).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            Assert.Fail(
                $"Embedded resource '{resourceName}' is missing from KyberWeave.Core. Declare it as an " +
                $"EmbeddedResource with LogicalName=\"{resourceName}\" next to the Standards.<tech>.md entries.");
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static YamlMappingNode LoadFamily(string family)
    {
        YamlStream yaml = new();
        using StringReader reader = new(ReadFamilyText(family));
        yaml.Load(reader);

        Assert.Single(yaml.Documents);
        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    private static IEnumerable<CatalogueGate> EnumerateGates(YamlMappingNode root)
    {
        foreach (string section in new[] { "gates", "task-gates" })
        {
            if (!root.Children.TryGetValue(new YamlScalarNode(section), out YamlNode? node))
            {
                continue;
            }

            foreach (YamlNode item in Assert.IsType<YamlSequenceNode>(node).Children)
            {
                YamlMappingNode gate = Assert.IsType<YamlMappingNode>(item);
                yield return new CatalogueGate(section, Scalar(gate, "id") ?? "<no id>", gate);
            }
        }
    }

    private static bool RunsNodeTool(YamlMappingNode gate)
    {
        foreach (string key in ArgvKeys)
        {
            if (Child(gate, key) is YamlSequenceNode argv &&
                argv.Children.Count > 0 &&
                argv.Children[0] is YamlScalarNode program &&
                program.Value is ("npm" or "npx"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOptional(YamlMappingNode gate)
    {
        if (Scalar(gate, "optional") == "true")
        {
            return true;
        }

        return Child(gate, "when") switch
        {
            YamlScalarNode { Value: "optional" } => true,
            YamlMappingNode condition => Scalar(condition, "optional") == "true",
            _ => false
        };
    }

    private static string Label(CatalogueGate gate) => $"{gate.Section} '{gate.Id}'";

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) ? node : null;

    private static string? Scalar(YamlMappingNode map, string key) =>
        Child(map, key) is YamlScalarNode scalar ? scalar.Value : null;
}
