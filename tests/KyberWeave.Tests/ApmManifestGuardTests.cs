using System.Text.RegularExpressions;
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
    /// Matches this repository's owner/name in every form APM accepts for a GitHub package:
    /// shorthand, <c>github.com/</c>, <c>https://</c>/<c>ssh://</c> URLs, and <c>git@</c>
    /// SCP-style remotes, with an optional <c>.git</c> suffix and trailing slashes.
    /// </summary>
    private static readonly Regex SelfDependency = new(
        @"^(?:(?:https?|ssh|git)://(?:[^@/]+@)?(?:www\.)?github\.com/|(?:[^@/]+@)?github\.com:|(?:www\.)?github\.com/)?dpalfery/kyber-weave(?:\.git)?/*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        YamlMappingNode root = ParseManifest(File.ReadAllText(ApmManifestPath));

        IReadOnlyList<string> offenders = FindSelfDependencies(root);

        Assert.True(
            offenders.Count == 0,
            "apm.yml must not declare dpalfery/kyber-weave as its own dependency " +
            "(circular dependency aborts `apm install` and the docs init skill deployment, " +
            $"issue #285): found {string.Join(", ", offenders)}.");
    }

    [Theory]
    [InlineData("dpalfery/kyber-weave")]
    [InlineData("dpalfery/kyber-weave#v0.1.1")]
    [InlineData("dpalfery/kyber-weave.git")]
    [InlineData("dpalfery/kyber-weave/")]
    [InlineData("\"  dpalfery/kyber-weave  \"")]
    [InlineData("github.com/dpalfery/kyber-weave")]
    [InlineData("https://github.com/dpalfery/kyber-weave")]
    [InlineData("https://github.com/dpalfery/kyber-weave.git")]
    [InlineData("https://github.com/dpalfery/kyber-weave/")]
    [InlineData("https://github.com/dpalfery/kyber-weave.git#main")]
    [InlineData("https://www.github.com/DPALFERY/Kyber-Weave.git")]
    [InlineData("git@github.com:dpalfery/kyber-weave.git")]
    [InlineData("ssh://git@github.com/dpalfery/kyber-weave.git")]
    [InlineData("git: https://github.com/dpalfery/kyber-weave.git")]
    [InlineData("git: dpalfery/kyber-weave")]
    public void EverySelfDependencyFormInApmDependenciesIsDetected(string entry)
    {
        YamlMappingNode root = ParseManifest(ManifestWithApmEntry("dependencies", entry));

        Assert.NotEmpty(FindSelfDependencies(root));
    }

    [Fact]
    public void SelfDependencyInDevDependenciesIsDetected()
    {
        YamlMappingNode root = ParseManifest(
            ManifestWithApmEntry("devDependencies", "https://github.com/dpalfery/kyber-weave.git"));

        Assert.NotEmpty(FindSelfDependencies(root));
    }

    [Theory]
    [InlineData("dpalfery/kyber-weave-docs")]
    [InlineData("dpalfery/kyber-weave-docs#v1")]
    [InlineData("dpalfery/kyber-weaver")]
    [InlineData("someone-else/kyber-weave")]
    [InlineData("https://github.com/dpalfery/kyber-weave-docs.git")]
    [InlineData("https://github.com/someone-else/kyber-weave")]
    [InlineData("https://gitlab.com/dpalfery/kyber-weave")]
    [InlineData("git: https://github.com/dpalfery/kyber-weave-docs")]
    public void OtherPackagesAreNotFlaggedAsSelfDependencies(string entry)
    {
        YamlMappingNode root = ParseManifest(ManifestWithApmEntry("dependencies", entry));

        Assert.Empty(FindSelfDependencies(root));
    }

    /// <summary>
    /// A malformed manifest must fail with a readable assertion, not an
    /// <c>IndexOutOfRangeException</c> or <c>InvalidCastException</c> from the loader.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("just a scalar")]
    [InlineData("- a\n- b\n")]
    [InlineData("name: a\n---\nname: b\n")]
    public void MalformedManifestFailsWithAReadableAssertion(string text)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ParseManifest(text));
    }

    [Fact]
    public void TheRepositoryManifestPinsAgentSkillsTargets()
    {
        YamlMappingNode root = ParseManifest(File.ReadAllText(ApmManifestPath));

        Assert.True(
            root.Children.TryGetValue(new YamlScalarNode("targets"), out YamlNode? targetsNode),
            "apm.yml must pin targets (see the why-comment above it).");
        YamlSequenceNode targets = Assert.IsType<YamlSequenceNode>(targetsNode);
        string[] values = targets.Children
            .Select(c => Assert.IsType<YamlScalarNode>(c).Value!)
            .ToArray();
        Assert.Equal(["agent-skills"], values);
    }

    private static YamlMappingNode ParseManifest(string text)
    {
        YamlStream stream = new();
        using StringReader reader = new(text);
        stream.Load(reader);

        Assert.Single(stream.Documents);
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static string ManifestWithApmEntry(string section, string entry) =>
        $"{section}:\n  apm:\n    - {entry}\n";

    private static IReadOnlyList<string> FindSelfDependencies(YamlMappingNode root)
    {
        List<string> offenders = [];

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
                if (offender.Length > 0)
                    offenders.Add($"{section}.apm '{offender}'");
            }
        }

        return offenders;
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

        string withoutRef = value.Split('#', 2)[0].Trim();
        return SelfDependency.IsMatch(withoutRef);
    }
}
