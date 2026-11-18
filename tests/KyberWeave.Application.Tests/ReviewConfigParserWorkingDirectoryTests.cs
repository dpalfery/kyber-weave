using KyberWeave.Core.Configuration;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// How a gate's <c>working-directory</c> is checked through the facts the caller supplies: the parser asks them for
/// the full path, so it reads no file system and no process state of its own.
/// </summary>
public sealed class ReviewConfigParserWorkingDirectoryTests
{
    private static readonly ReviewYamlSection Section = new()
    {
        Gates = [new ReviewGateYaml { Id = "build", Run = ["dotnet", "build"], WorkingDirectory = "web" }]
    };

    private static readonly string Absolute = OperatingSystem.IsWindows() ? @"C:\base\repo" : "/base/repo";

    [Fact]
    public void ADirectoryTheFactsSayExistsIsAcceptedUnderTheResolvedRoot()
    {
        List<string> asked = [];
        ReviewWorkingDirectoryFacts facts = Facts(directoryExists: path => { asked.Add(path); return true; });

        ReviewConfig config = Merge("repo", facts);

        Assert.Equal("web", Assert.Single(config.Gates).WorkingDirectory);
        Assert.Equal([Path.Combine(Absolute, "web")], asked);
    }

    [Fact]
    public void ADirectoryTheFactsSayIsMissingIsRefused()
    {
        ReviewWorkingDirectoryFacts facts = Facts(directoryExists: _ => false);

        YamlException refusal = Assert.Throws<YamlException>(() => Merge("repo", facts));

        Assert.Contains("does not exist", refusal.Message, StringComparison.Ordinal);
    }

    private static ReviewConfig Merge(string root, ReviewWorkingDirectoryFacts facts) =>
        ReviewConfigParser.Merge(
            ReviewConfig.ProductDefaults,
            Section,
            root,
            null,
            facts);

    private static ReviewWorkingDirectoryFacts Facts(Func<string, bool> directoryExists) =>
        new(directoryExists, (_, _) => false, path => Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(Absolute)!, path));
}
