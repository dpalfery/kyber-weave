using System.Text;
using KyberWeave.Cli.Commands;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>mcp:</c> declaration section of <c>kyber-weave.yml</c>: the servers a repository's
/// agent instructions rely on, their launch command as argv, and their provenance.
/// </summary>
/// <remarks>
/// Two properties here are the reason the section exists, and both are asserted as behaviour
/// rather than as shape. First, an absent or empty section must change nothing at all: every
/// host that upgrades without declaring an MCP server keeps running exactly as it does today.
/// Second, a misspelt <c>provenance</c> or <c>level</c> is refused at load time through the
/// existing <c>KW-CONFIG-001</c> path, because the alternative — falling back to a default
/// enum member — turns a typo into "this server is first-party and required", which is a
/// silent change to what the repository claims to rely on. The merge cases below declare their
/// invented inherited server third-party for that same reason: <c>first-party</c> is a claim
/// about the binary Kyber-Weave ships, and a made-up binary name cannot support it.
/// </remarks>
public sealed class McpConfigTests : IDisposable
{
    /// <summary>Today's first-party server: the binary Kyber-Weave itself ships.</summary>
    private const string FirstPartyServer = "kyber-weave";

    /// <summary>Today's third-party server: a binary the user installs.</summary>
    private const string ThirdPartyServer = "codegraph";

    private const string ConfigFileName = "kyber-weave.yml";

    private readonly TempDirectory _root = new();

    /// <summary>Deletes the repository tree the file-backed cases wrote.</summary>
    public void Dispose() => _root.Dispose();

    /// <summary>
    /// Every key of the design's worked example reaches the model unchanged, so a host can copy
    /// the documented YAML and get the declaration it read about.
    /// </summary>
    [Fact]
    public void AFullSectionParsesEveryDeclaredField()
    {
        McpConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            mcp:
              required-servers:
                kyber-weave:
                  command: [kyber-weave-mcp, --repo-root, .]
                  provenance: first-party
                  level: required
                  install: dotnet tool install --global KyberWeave.Mcp
                codegraph:
                  command: [codegraph, serve, --mcp]
                  provenance: third-party
                  level: recommended
                  install: codegraph init .
            """).Mcp;

        Assert.Equal(2, config.RequiredServers.Count);

        RequiredMcpServer firstParty = config.RequiredServers[FirstPartyServer];
        Assert.Equal(FirstPartyServer, firstParty.Name);
        Assert.Equal(["kyber-weave-mcp", "--repo-root", "."], firstParty.Command);
        Assert.Equal(McpProvenance.FirstParty, firstParty.Provenance);
        Assert.Equal(McpRequirementLevel.Required, firstParty.Level);
        Assert.Equal("dotnet tool install --global KyberWeave.Mcp", firstParty.Install);

        RequiredMcpServer thirdParty = config.RequiredServers[ThirdPartyServer];
        Assert.Equal(ThirdPartyServer, thirdParty.Name);
        Assert.Equal(["codegraph", "serve", "--mcp"], thirdParty.Command);
        Assert.Equal(McpProvenance.ThirdParty, thirdParty.Provenance);
        Assert.Equal(McpRequirementLevel.Recommended, thirdParty.Level);
        Assert.Equal("codegraph init .", thirdParty.Install);
    }

    /// <summary>
    /// An entry that names no level is required (R1.2). The default is the strict reading on
    /// purpose: a server an instruction depends on and an operator forgot to classify is still
    /// one whose absence breaks the instruction.
    /// </summary>
    [Fact]
    public void AnEntryWithoutALevelIsTreatedAsRequired()
    {
        McpConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            mcp:
              required-servers:
                codegraph:
                  command: [codegraph, serve, --mcp]
                  provenance: third-party
            """).Mcp;

        Assert.Equal(McpRequirementLevel.Required, config.RequiredServers[ThirdPartyServer].Level);
    }

    /// <summary>
    /// The argv is carried as a list, never re-split or re-joined, and its declared order is the
    /// order a launch would use. Sorted or normalized argv would run a different command.
    /// </summary>
    [Fact]
    public void ArgvOrderIsPreserved()
    {
        McpConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            mcp:
              required-servers:
                codegraph:
                  command: [codegraph, --quiet, serve, --repo-root, ., --mcp]
                  provenance: third-party
            """).Mcp;

        Assert.Equal(
            ["codegraph", "--quiet", "serve", "--repo-root", ".", "--mcp"],
            config.RequiredServers[ThirdPartyServer].Command);
    }

    /// <summary>
    /// <c>install</c> is an optional manual hint printed verbatim on the absent-binary path; it
    /// is never part of the launch command, so declaring one must leave the argv untouched.
    /// </summary>
    [Fact]
    public void InstallIsOptionalAndKeptVerbatimWithoutTouchingTheArgv()
    {
        McpConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            mcp:
              required-servers:
                kyber-weave:
                  command: [kyber-weave-mcp, --repo-root, .]
                  provenance: first-party
                codegraph:
                  command: [codegraph, serve, --mcp]
                  provenance: third-party
                  install: codegraph init .
            """).Mcp;

        Assert.Null(config.RequiredServers[FirstPartyServer].Install);

        RequiredMcpServer thirdParty = config.RequiredServers[ThirdPartyServer];
        Assert.Equal("codegraph init .", thirdParty.Install);
        Assert.Equal(["codegraph", "serve", "--mcp"], thirdParty.Command);
    }

    /// <summary>An absent section declares nothing, which is exactly today's behaviour (R1.4).</summary>
    [Theory]
    [InlineData("autonomy: autonomous\n")]
    [InlineData("ontology:\n  docs-root: docs\n")]
    [InlineData("review:\n  policy:\n    max-reviewable-lines: 400\n")]
    public void AnAbsentSectionDeclaresNoServers(string yaml)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml(yaml);

        Assert.Empty(config.Mcp.RequiredServers);
    }

    /// <summary>
    /// A section key that is present with nothing under it declares nothing either, so a host
    /// that scaffolds the section before filling it in does not start reporting findings.
    /// </summary>
    [Theory]
    [InlineData("mcp:\n")]
    [InlineData("mcp: {}\n")]
    [InlineData("mcp:\n  required-servers:\n")]
    [InlineData("mcp:\n  required-servers: {}\n")]
    public void AnEmptySectionDeclaresNoServers(string yaml)
    {
        KyberWeaveConfig config = KyberWeaveConfigLoader.LoadFromYaml(yaml);

        Assert.Empty(config.Mcp.RequiredServers);
    }

    /// <summary>
    /// The product defaults declare no server. Everything downstream keys on this: the check
    /// reports nothing and <c>docs init</c> behaves as it does today until a host opts in.
    /// </summary>
    [Fact]
    public void ProductDefaultsDeclareNoServers()
    {
        Assert.Empty(McpConfig.ProductDefaults.RequiredServers);
    }

    /// <summary>
    /// The repository's configuration rule is that a map replaces rather than merges. An
    /// operator who declares one server must end up with one server, not with the inherited
    /// ones plus their own — otherwise a server they removed keeps being reported missing.
    /// </summary>
    [Fact]
    public void TheHostsServerMapReplacesTheDefaultsRatherThanMerging()
    {
        McpConfig defaults = KyberWeaveConfigLoader.LoadFromYaml("""
            mcp:
              required-servers:
                inherited:
                  command: [inherited-mcp, --serve]
                  provenance: third-party
            """).Mcp;
        McpYamlSection? section = KyberWeaveYamlParser.Parse("""
            mcp:
              required-servers:
                codegraph:
                  command: [codegraph, serve, --mcp]
                  provenance: third-party
            """).Mcp;

        McpConfig merged = McpConfigLoader.Merge(defaults, section);

        KeyValuePair<string, RequiredMcpServer> declared = Assert.Single(merged.RequiredServers);
        Assert.Equal(ThirdPartyServer, declared.Key);
        Assert.Equal(["codegraph", "serve", "--mcp"], declared.Value.Command);
    }

    /// <summary>
    /// No section at all falls back to the defaults it was merged over, rather than clearing
    /// them: the same "null falls back, a declared value replaces" rule every other section
    /// follows.
    /// </summary>
    [Fact]
    public void TheServerMapFallsBackToTheDefaultsWhenTheSectionIsAbsent()
    {
        McpConfig defaults = KyberWeaveConfigLoader.LoadFromYaml("""
            mcp:
              required-servers:
                inherited:
                  command: [inherited-mcp, --serve]
                  provenance: third-party
            """).Mcp;
        McpYamlSection? section = KyberWeaveYamlParser.Parse("autonomy: autonomous\n").Mcp;

        McpConfig merged = McpConfigLoader.Merge(defaults, section);

        RequiredMcpServer inherited = Assert.Single(merged.RequiredServers).Value;
        Assert.Equal("inherited", inherited.Name);
        Assert.Equal(["inherited-mcp", "--serve"], inherited.Command);
    }

    /// <summary>
    /// A provenance outside the closed vocabulary is a configuration error, not a default. The
    /// refusal names the section, the server and the offending value, so the operator does not
    /// have to search the file for the typo.
    /// </summary>
    [Theory]
    [InlineData("firstparty")]
    [InlineData("third_party")]
    [InlineData("vendor")]
    [InlineData("mixed")]
    public void AMisspelledProvenanceIsRefusedAsKwConfig001(string provenance)
    {
        string configPath = WriteRepositoryConfig(ServerEntryYaml(
            ThirdPartyServer,
            "command: [codegraph, serve, --mcp]",
            $"provenance: {provenance}"));

        AssertRefusedAsConfigError(configPath, provenance);
    }

    /// <summary>
    /// A level outside the closed vocabulary is refused for the same reason: silently reading
    /// <c>optional</c> as <c>required</c> would report a finding the operator opted out of.
    /// </summary>
    [Theory]
    [InlineData("optional")]
    [InlineData("must")]
    [InlineData("needed")]
    public void AMisspelledLevelIsRefusedAsKwConfig001(string level)
    {
        string configPath = WriteRepositoryConfig(ServerEntryYaml(
            ThirdPartyServer,
            "command: [codegraph, serve, --mcp]",
            "provenance: third-party",
            $"level: {level}"));

        AssertRefusedAsConfigError(configPath, level);
    }

    /// <summary>
    /// R1.1 makes the launch command and the provenance class mandatory on every entry. An
    /// entry missing one is malformed, so it is refused where the operator wrote it rather
    /// than defaulting to a binary nobody named.
    /// </summary>
    [Theory]
    [InlineData("command")]
    [InlineData("provenance")]
    public void AnEntryMissingARequiredFieldIsRefusedAsKwConfig001(string missingField)
    {
        List<string> lines = [];
        if (!string.Equals(missingField, "command", StringComparison.Ordinal))
            lines.Add("command: [codegraph, serve, --mcp]");
        if (!string.Equals(missingField, "provenance", StringComparison.Ordinal))
            lines.Add("provenance: third-party");

        string configPath = WriteRepositoryConfig(ServerEntryYaml(ThirdPartyServer, [.. lines]));

        AssertRefusedAsConfigError(configPath, missingField);
    }

    /// <summary>
    /// Builds a one-server configuration. Written as text because the loader under test is the
    /// only thing that may interpret it.
    /// </summary>
    private static string ServerEntryYaml(string serverName, params string[] lines)
    {
        StringBuilder yaml = new();
        yaml.Append("mcp:\n  required-servers:\n    ").Append(serverName).Append(":\n");
        foreach (string line in lines)
            yaml.Append("      ").Append(line).Append('\n');

        return yaml.ToString();
    }

    private string WriteRepositoryConfig(string yaml)
    {
        string directory = Path.Combine(_root.Path, ".kyber-weave");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ConfigFileName);
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>
    /// Asserts the refusal at both seams: the structured load result, and the
    /// <c>KW-CONFIG-001</c> diagnostic a command turns it into.
    /// </summary>
    private void AssertRefusedAsConfigError(string configPath, string offender)
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(_root.Path);

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.Equal(configPath, result.ConfigPath);

        string error = result.Error ?? string.Empty;
        Assert.Contains("mcp.required-servers", error, StringComparison.Ordinal);
        Assert.Contains(ThirdPartyServer, error, StringComparison.Ordinal);
        Assert.Contains(offender, error, StringComparison.Ordinal);

        DiagnosticReport report = new();
        bool loaded = CommandHelpers.TryLoadConfig(_root.Path, null, report, out KyberWeaveConfig config);

        Assert.False(loaded);
        Assert.Equal(KyberWeaveConfig.ProductDefaults, config);
        Diagnostic diagnostic = Assert.Single(report.Items);
        Assert.Equal(KyberWeaveConfigLoader.ConfigLoadErrorCode, diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Contains(offender, diagnostic.Message, StringComparison.Ordinal);
    }
}
