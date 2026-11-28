using System.Reflection;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Cli.Tests.Commands.Docs;

/// <summary>
/// Verifies the docs init command execution, CLI option binding, and delegation to the scaffolder.
/// </summary>
public sealed class DocsInitCommandTests
{
    private readonly MemoryDocsHost _host = new();

    /// <summary>
    /// Verifies that DocsInitSettings binds the --kyber-standards command option, defaults to false,
    /// and that passing the option triggers scaffolding of all 11 Kyber Squad rich standards.
    /// </summary>
    [Fact]
    public async Task ExecuteWithKyberStandardsOptionPassesKyberStandardsFlagToScaffolder()
    {
        // 1. DocsInitSettings has KyberStandards property with [CommandOption("--kyber-standards")]
        PropertyInfo? property = typeof(DocsInitSettings).GetProperty(nameof(DocsInitSettings.KyberStandards));
        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property.PropertyType);

        CommandOptionAttribute? optionAttribute = property
            .GetCustomAttributes(typeof(CommandOptionAttribute), inherit: true)
            .Cast<CommandOptionAttribute>()
            .FirstOrDefault();
        Assert.NotNull(optionAttribute);
        Assert.Contains("kyber-standards", optionAttribute.LongNames);

        DocsInitSettings defaultSettings = new DocsInitSettings();
        Assert.False(defaultSettings.KyberStandards);

        // 2. CommandApp binds --kyber-standards from command line arguments
        CommandApp app = CommandAppFixture.DocsInit(_host.Register, _host.Console);

        int exitCode = await app.RunAsync(["docs", "init", _host.Repo, "--kyber-standards", "--no-skill"]);
        Assert.Equal(0, exitCode);

        // 3. Verifies that every shipped standard was created
        foreach (string tech in KyberStandardsTemplates.All)
        {
            string standardPath = Path.Combine(_host.Repo, "docs", "standards", tech, $"{tech}.standards.md");
            Assert.True(_host.Files.FileExists(standardPath), $"Expected standard for '{tech}' to exist at '{standardPath}'.");
            string content = _host.Files.ReadAllText(standardPath);
            Assert.Contains($"technology: {tech}", content, StringComparison.Ordinal);
            Assert.Contains("doc-type: coding-standard", content, StringComparison.Ordinal);
        }

        // 4. DocsInitCommand.TryScaffold forwards KyberStandards flag
        string second = Path.Combine(_host.Files.Root, "second");
        _host.Files.CreateDirectory(second);
        DocsInitSettings initSettings = new DocsInitSettings
        {
            Path = second,
            KyberStandards = true,
            NoSkill = true
        };

        (int tryExitCode, ScaffoldResult? result, string? error) = await DocsInitCommand.TryScaffoldAsync(
            _host.Services().GetRequiredService<IDocsScaffoldingService>(),
            initSettings,
            CancellationToken.None);
        Assert.Equal(0, tryExitCode);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(
            KyberStandardsTemplates.All.Count,
            result.Files.Count(f => f.Outcome == ScaffoldOutcome.Created
                && KyberStandardsTemplates.All.Any(tech => string.Equals(
                    f.RelativePath,
                    $"docs/standards/{tech}/{tech}.standards.md",
                    StringComparison.Ordinal))));
    }
}
