using System.Reflection;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Core.Docs.Scaffolding;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Verifies the docs init command execution, CLI option binding, and delegation to the scaffolder.
/// </summary>
public sealed class DocsInitCommandTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Verifies that DocsInitSettings binds the --kyber-standards command option, defaults to false,
    /// and that passing the option triggers scaffolding of all 10 Kyber Squad rich standards.
    /// </summary>
    [Fact]
    public void ExecuteWithKyberStandardsOptionPassesKyberStandardsFlagToScaffolder()
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
        CommandApp app = new CommandApp();
        app.Configure(config =>
        {
            config.AddBranch("docs", docs =>
            {
                docs.AddCommand<DocsInitCommand>("init");
            });
        });

        int exitCode = app.Run(["docs", "init", _temp.Path, "--kyber-standards", "--no-skill"]);
        Assert.Equal(0, exitCode);

        // 3. Verifies that all 10 rich standards were created
        foreach (string tech in KyberStandardsTemplates.All)
        {
            string standardPath = Path.Combine(_temp.Path, "docs", "standards", tech, "README.md");
            Assert.True(File.Exists(standardPath), $"Expected standard for '{tech}' to exist at '{standardPath}'.");
            string content = File.ReadAllText(standardPath);
            Assert.Contains($"technology: {tech}", content, StringComparison.Ordinal);
            Assert.Contains("doc-type: coding-standard", content, StringComparison.Ordinal);
        }

        // 4. DocsInitCommand.TryScaffold forwards KyberStandards flag
        using TempDirectory secondTemp = new TempDirectory();
        DocsInitSettings initSettings = new DocsInitSettings
        {
            Path = secondTemp.Path,
            KyberStandards = true,
            NoSkill = true
        };

        (int tryExitCode, ScaffoldResult? result, string? error) = DocsInitCommand.TryScaffold(initSettings);
        Assert.Equal(0, tryExitCode);
        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal(
            KyberStandardsTemplates.All.Count,
            result.Files.Count(f => f.RelativePath.StartsWith("docs/standards/", StringComparison.Ordinal)
                && f.RelativePath != "docs/standards/README.md"
                && f.RelativePath.EndsWith("/README.md", StringComparison.Ordinal)
                && f.Outcome == ScaffoldOutcome.Created));
    }
}
