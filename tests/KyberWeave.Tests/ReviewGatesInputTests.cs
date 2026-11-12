using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The <c>review gates</c> operands that name nothing: a path, <c>--config</c>, <c>--out</c> or <c>--agent</c> given an empty or
/// blank value. Each is refused as bad input (KW-REVIEW-028, exit 2) naming the option, before any gate runs.
/// </summary>
public sealed class ReviewGatesInputTests : IDisposable
{
    private const string InvalidInput = "KW-REVIEW-028";
    private const int ClientInputExitCode = 2;
    private const string Marker = "ran.marker";

    private readonly TempDirectory _temp = new();
    private readonly string _repo;

    public ReviewGatesInputTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));

        // The gate writes a marker when it runs, so a refusal that comes too late is visible as the marker's existence.
        File.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "review:\n  gates:\n    - id: probe\n      run: [touch, " + Marker + "]\n");
    }

    /// <summary>Deletes the temporary repository.</summary>
    public void Dispose()
    {
        _temp.Dispose();
    }

    [Theory]
    [InlineData("[path]", "")]
    [InlineData("[path]", "   ")]
    [InlineData("--config", "")]
    [InlineData("--config", "   ")]
    [InlineData("--out", "")]
    [InlineData("--out", "   ")]
    [InlineData("--agent", "")]
    [InlineData("--agent", "   ")]
    public void AnEmptyOperandIsRefusedNamingTheOptionBeforeAnyGateRuns(string option, string value)
    {
        string[] arguments = option == "[path]"
            ? ["review", "gates", value, "--format", "json"]
            : ["review", "gates", _repo, option, value, "--format", "json"];

        CapturedConsoleExecution<int> run = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(arguments));

        Assert.Equal(ClientInputExitCode, run.Result);
        Assert.Contains(InvalidInput, run.Output, StringComparison.Ordinal);
        Assert.Contains(option, run.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_repo, Marker)), "A gate ran after an empty operand was refused.");
    }
}
