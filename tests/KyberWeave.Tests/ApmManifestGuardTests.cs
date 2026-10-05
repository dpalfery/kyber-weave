using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the GitHub issue #285 invariant for the repo-root <c>apm.yml</c> manifest: the
/// package must never declare itself as a dependency, and the <c>targets</c> pin must
/// stay exactly <c>[agent-skills]</c>.
/// </summary>
/// <remarks>
/// The APM loader walks <c>dependencies.apm</c>/<c>devDependencies.apm</c> in its Resolve
/// phase and aborts with "Cannot install packages with circular dependencies" when a
/// package depends on itself, so <c>docs init</c>'s skill deployment silently degrades in
/// every consuming repository. The dependency walk lives in the external APM binary; this
/// repository guards the manifest because the manifest is the defect.
/// </remarks>
public sealed class ApmManifestGuardTests
{
    private static readonly string ApmManifestPath =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "apm.yml");

    /// <summary>
    /// A self-dependency entry — in either <c>dependencies.apm</c> or
    /// <c>devDependencies.apm</c>, string form with or without a <c>#ref</c> suffix, or
    /// object form carrying <c>git: dpalfery/kyber-weave</c> — makes every external
    /// <c>apm install dpalfery/kyber-weave</c> abort on a circular dependency before any
    /// file is written, which is exactly how <c>docs init</c>'s skill deployment broke
    /// (issue #285). The manifest must declare no self-dependency anywhere.
    /// </summary>
    [Fact]
    public void TheRepositoryManifestDeclaresNoApmSelfDependency()
    {
        YamlMappingNode root = LoadManifest();

        foreach (string section in new[] { "dependencies", "devDependencies" })
        {
            if (!TryGetSection(root, section, out YamlMappingNode? deps) || deps is null)
                continue;

            if (!deps.Children.TryGetValue(new YamlScalarNode("apm"), out YamlNode? apmNode) ||
                apmNode is not YamlSequenceNode apmEntries)
                continue;

            foreach (YamlNode entry in apmEntries.Children)
            {
                string offender = IdentifyOffender(entry);
                Assert.True(
                    offender.Length == 0,
                    $"apm.yml {section}.apm must not declare dpalfery/kyber-weave as its own " +
                    $"dependency (circular dependency aborts `apm install` and the docs init " +
                    $"skill deployment, issue #285): found '{offender}'.");
            }
        }
    }

    /// <summary>
    /// The <c>targets: [agent-skills]</c> pin is a deliberate, documented non-negotiable
    /// (root AGENTS.md): it scopes <c>apm compile</c>/install outputs to
    /// <c>.agents/skills/</c> so the hand-authored AGENTS.md files stay untouched. This
    /// fix keeps the pin byte-for-byte; the guard holds it in place.
    /// </summary>
    [Fact]
    public void TheRepositoryManifestPinsAgentSkillsTargets()
    {
        YamlMappingNode root = LoadManifest();

        Assert.True(
            root.Children.TryGetValue(new YamlScalarNode("targets"), out YamlNode? targetsNode),
            "apm.yml must pin targets (see the why-comment above it).");
        YamlSequenceNode targets = Assert.IsType<YamlSequenceNode>(targetsNode);
        string[] values = targets.Children
            .Select(c => Assert.IsType<YamlScalarNode>(c).Value!)
            .ToArray();
        Assert.Equal(["agent-skills"], values);
    }

    private static YamlMappingNode LoadManifest()
    {
        YamlStream stream = new();
        using StringReader reader = new(File.ReadAllText(ApmManifestPath));
        stream.Load(reader);
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static bool TryGetSection(
        YamlMappingNode root,
        string name,
        out YamlMappingNode? section)
    {
        if (root.Children.TryGetValue(new YamlScalarNode(name), out YamlNode? node) &&
            node is YamlMappingNode mapping)
        {
            section = mapping;
            return true;
        }

        section = null;
        return false;
    }

    private static string IdentifyOffender(YamlNode entry)
    {
        switch (entry)
        {
            case YamlScalarNode scalar when IsSelfDependency(scalar.Value):
                return scalar.Value!;
            case YamlMappingNode mapping
                when mapping.Children.TryGetValue(new YamlScalarNode("git"), out YamlNode? gitNode) &&
                     gitNode is YamlScalarNode gitScalar &&
                     IsSelfDependency(gitScalar.Value):
                return gitScalar.Value!;
            default:
                return string.Empty;
        }
    }

    private static bool IsSelfDependency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string withoutRef = value.Split('#', 2)[0].TrimEnd();
        return string.Equals(withoutRef, "dpalfery/kyber-weave", StringComparison.OrdinalIgnoreCase);
    }
}
