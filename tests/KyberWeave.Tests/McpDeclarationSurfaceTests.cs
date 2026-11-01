using System.Text;
using KyberWeave.Cli.Commands;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The declaration guards an operator can trip from <c>kyber-weave.yml</c> itself: the name they
/// write as the map key, and writing one twice.
/// </summary>
/// <remarks>
/// Every case loads a real document from disk rather than handing the loader a built section. The
/// map key is the server name a harness entry is written under, a diagnostic names, and Codex turns
/// into a TOML table header, so what is wrong with a blank or repeated one only shows up on the
/// path that reads the file.
/// </remarks>
public sealed class McpDeclarationSurfaceTests : IDisposable
{
    private const string ConfigFileName = "kyber-weave.yml";
    private const string SectionPath = "mcp.required-servers";
    private const string ThirdPartyServer = "codegraph";

    private readonly TempDirectory _root = new();

    /// <summary>Deletes the repository tree the cases wrote.</summary>
    public void Dispose() => _root.Dispose();

    /// <summary>
    /// A blank key names nothing, so it is refused where it was written rather than carried into
    /// the entry key, the diagnostic subject and the TOML header that all quote it.
    /// </summary>
    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void ADeclarationWithABlankServerNameIsRefusedAsKwConfig001(string nameKey)
    {
        string configPath = WriteDeclaration(
            nameKey,
            "command: [codegraph, serve, --mcp]",
            "provenance: third-party");

        string error = AssertRefusedAsConfigError(configPath);

        Assert.Contains(SectionPath, error, StringComparison.Ordinal);
        Assert.Contains("blank name", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two entries under one key are one entry once the document is bound, so which of the two the
    /// operator meant cannot be recovered from the map. The document is refused instead of read as
    /// a declaration of whichever entry happened to come last.
    /// </summary>
    /// <remarks>
    /// The refusal itself predates this guard: the shared deserializer refuses a repeated key
    /// anywhere in the document, and the message names the key but not its path — a section-aware
    /// message would have to come from that deserializer, not from this loader, which never sees the
    /// second entry. The case is pinned because the behaviour is what keeps a duplicate from being
    /// silently collapsed, and nothing in this suite could observe that otherwise.
    /// </remarks>
    [Fact]
    public void ADuplicateServerNameIsRefusedAsKwConfig001()
    {
        string configPath = WriteRepositoryConfig($"""
            mcp:
              required-servers:
                {ThirdPartyServer}:
                  command: [codegraph, serve, --mcp]
                  provenance: third-party
                {ThirdPartyServer}:
                  command: [codegraph, serve, --mcp, --quiet]
                  provenance: third-party
            """);

        string error = AssertRefusedAsConfigError(configPath);

        Assert.Contains(ThirdPartyServer, error, StringComparison.Ordinal);
    }

    /// <summary>Builds a one-server declaration whose entry body is the given lines.</summary>
    private string WriteDeclaration(string nameKey, params string[] body)
    {
        StringBuilder yaml = new();
        yaml.Append("mcp:\n  required-servers:\n    ").Append(nameKey).Append(":\n");
        foreach (string line in body)
            yaml.Append("      ").Append(line).Append('\n');

        return WriteRepositoryConfig(yaml.ToString());
    }

    private string WriteRepositoryConfig(string yaml)
    {
        string directory = Path.Combine(_root.Path, ".kyber-weave");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, ConfigFileName);
        File.WriteAllText(path, yaml);
        return path;
    }

    /// <summary>Takes the refusal through the <c>KW-CONFIG-001</c> path and returns the message.</summary>
    private string AssertRefusedAsConfigError(string configPath)
    {
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(_root.Path);

        Assert.False(result.Success, "the declaration must be refused");
        Assert.Null(result.Config);
        Assert.Equal(configPath, result.ConfigPath);

        DiagnosticReport report = new();
        bool loaded = CommandHelpers.TryLoadConfig(_root.Path, null, report, out KyberWeaveConfig config);

        Assert.False(loaded);
        Assert.Equal(KyberWeaveConfig.ProductDefaults, config);
        Diagnostic diagnostic = Assert.Single(report.Items);
        Assert.Equal(KyberWeaveConfigLoader.ConfigLoadErrorCode, diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);

        return result.Error ?? string.Empty;
    }
}
