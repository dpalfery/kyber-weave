using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>docs init</c> over the scaffolder on an in-memory checkout: a broken host configuration ends as the command's
/// handled error, not an unhandled one.
/// </summary>
public sealed class DocsInitPreflightTests
{
    private readonly MemoryScaffoldHost _host = new();

    /// <summary>
    /// The CLI must translate the preflight failure into its normal handled-error contract;
    /// otherwise Spectre reports an unhandled error and exits 255.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("handbook")]
    public async Task CliReturnsOneAndTheConfigDiagnosticWithoutWriting(string? docsRoot)
    {
        const string yaml = "ontology: [unclosed";
        _host.Write(".kyber-weave/kyber-weave.yml", yaml);

        (int ExitCode, ScaffoldResult? Result, string? Error) attempt = await DocsInitCommand.TryScaffoldAsync(
            _host.Scaffolder,
            new DocsInitSettings
            {
                Path = _host.Root,
                DocsRoot = docsRoot,
                NoSkill = true
            },
            CancellationToken.None);

        Assert.Equal(1, attempt.ExitCode);
        Assert.Null(attempt.Result);
        Assert.Contains(KyberWeaveConfigDiagnosticCodes.LoadError, attempt.Error, StringComparison.Ordinal);
        Assert.Contains("kyber-weave.yml", attempt.Error, StringComparison.Ordinal);
        Assert.Equal(yaml, _host.Read(".kyber-weave/kyber-weave.yml"));
        Assert.Single(_host.AllFiles());
    }
}
