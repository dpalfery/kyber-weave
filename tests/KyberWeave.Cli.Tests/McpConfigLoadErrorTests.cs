using System.Text;
using KyberWeave.Cli.Commands;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// A misspelt or incomplete <c>mcp:</c> declaration is refused through the existing <c>KW-CONFIG-001</c> path, so a
/// typo never becomes a silent default.
/// </summary>
public sealed class McpConfigLoadErrorTests
{
    private const string ThirdPartyServer = "codegraph";

    private readonly InMemoryFileSystem _files = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly string _root;

    public McpConfigLoadErrorTests() => _root = _files.CreateTemporaryDirectory("kw-mcp-config-");

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
        _configs.WriteRepositoryConfig(_root, yaml);
        return FakeKyberWeaveConfigRepository.RepositoryConfigPath(_root);
    }

    /// <summary>
    /// Asserts the refusal at both seams: the structured load result, and the
    /// <c>KW-CONFIG-001</c> diagnostic a command turns it into.
    /// </summary>
    private void AssertRefusedAsConfigError(string configPath, string offender)
    {
        KyberWeaveConfigLoadResult result = _configs.TryLoad(_root);

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.Equal(configPath, result.ConfigPath);

        string error = result.Error ?? string.Empty;
        Assert.Contains("mcp.required-servers", error, StringComparison.Ordinal);
        Assert.Contains(ThirdPartyServer, error, StringComparison.Ordinal);
        Assert.Contains(offender, error, StringComparison.Ordinal);

        DiagnosticReport report = new();
        bool loaded = CommandHelpers.TryLoadConfig(_configs, _root, null, report, out KyberWeaveConfig config);

        Assert.False(loaded);
        Assert.Equal(KyberWeaveConfigDefaults.Product, config);
        Diagnostic diagnostic = Assert.Single(report.Items);
        Assert.Equal(KyberWeaveConfigDiagnosticCodes.LoadError, diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Contains(offender, diagnostic.Message, StringComparison.Ordinal);
    }
}
