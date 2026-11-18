using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the shape and the budget of the test temp root. The redirect itself runs once, in a
///     module initializer, so the rule it applies is tested here as a pure function.
/// </summary>
public sealed class TestTempRootTests
{
    private const string Key = "0123456789abcdef";
    private const string DarwinUserTemp = "/var/folders/gg/jnm7dkgs7w1dhkb3j8wm4gg00000gn/T";
    private const string DarwinUserFolder = "/var/folders/gg/jnm7dkgs7w1dhkb3j8wm4gg00000gn";
    private const string MacBase = DarwinUserFolder + "/kw-test-temp";

    /// <summary>
    ///     macOS defaults to a folder beside the per-user temp directory, inside the tree an
    ///     endpoint scanner skips, so the shared temp directory is not an ancestor to list.
    /// </summary>
    [Fact]
    public void DefaultBasePathOnMacOSIsAFolderBesideTheUserTempDirectory()
    {
        Assert.Equal(
            MacBase,
            TestTempRoot.DefaultBasePath(isMacOS: true, DarwinUserTemp + "/"));
    }

    /// <summary>
    ///     Pins the reason for that placement: the base sits in the per-user folder, not under
    ///     the temp directory that every tool on the machine writes to.
    /// </summary>
    [Fact]
    public void TheMacDefaultBaseIsASiblingOfTheUserTempDirectoryNotAChild()
    {
        string basePath = TestTempRoot.DefaultBasePath(isMacOS: true, DarwinUserTemp);

        Assert.Equal(DarwinUserFolder, Path.GetDirectoryName(basePath));
        Assert.DoesNotContain(DarwinUserTemp + "/", basePath, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A temp directory with no separate parent leaves nowhere to put the sibling, so the
    ///     temp directory itself remains the base.
    /// </summary>
    [Fact]
    public void AMacTempDirectoryWithNoUsableParentStaysTheBase()
    {
        Assert.Equal("/T", TestTempRoot.DefaultBasePath(isMacOS: true, "/T"));
    }

    /// <summary>Every other Unix keeps the short system temp directory.</summary>
    [Fact]
    public void DefaultBasePathOffMacOSIsTheSystemTempDirectory()
    {
        Assert.Equal("/tmp", TestTempRoot.DefaultBasePath(isMacOS: false, DarwinUserTemp));
    }

    /// <summary>The macOS root is the same per-checkout, per-process shape under that base.</summary>
    [Fact]
    public void NoOverridePutsTheRootUnderTheMacBase()
    {
        string root = TestTempRoot.ResolveRoot(
            null,
            Key,
            4242,
            socketBudgetApplies: false,
            defaultBasePath: MacBase);

        Assert.Equal($"{MacBase}/kw-{Key}/4242", root);
    }

    /// <summary>Off macOS the unset override keeps today's behaviour: a short root under /tmp.</summary>
    [Fact]
    public void NoOverrideKeepsTheRootUnderTheSystemTempDirectoryOffMacOS()
    {
        string root = TestTempRoot.ResolveRoot(
            null,
            Key,
            4242,
            defaultBasePath: TestTempRoot.DefaultBasePath(isMacOS: false, DarwinUserTemp));

        Assert.Equal($"/tmp/kw-{Key}/4242", root);
    }

    /// <summary>
    ///     The default macOS root cannot carry the platform's control pipe, which is why the
    ///     redirect moves the pipe to the short directory.
    /// </summary>
    [Fact]
    public void TheMacRootIsTooLongForTheControlPipeSoThePipeMovesToTheShortDirectory()
    {
        string root = TestTempRoot.ResolveRoot(
            null,
            Key,
            4242,
            socketBudgetApplies: false,
            defaultBasePath: MacBase);

        Assert.True(root.Length > TestTempRoot.MaxRootLength);
        Assert.Equal("/tmp", TestTempRoot.ShortPipeDirectory);

        int pipePathLength =
            TestTempRoot.ShortPipeDirectory.Length + 1 + TestTempRoot.PipeNameLength;
        Assert.True(pipePathLength <= TestTempRoot.UnixSocketPathLimit);
        Assert.Equal(103, TestTempRoot.UnixSocketPathLimit);
        Assert.Equal(56, TestTempRoot.MaxRootLength);
    }

    /// <summary>
    ///     An absolute override moves the root under it, keeping the one-checkout, one-process
    ///     component so two runs on the same volume cannot see each other.
    /// </summary>
    [Fact]
    public void AnAbsoluteOverrideMovesTheRootUnderIt()
    {
        string root = TestTempRoot.ResolveRoot("/Volumes/kwramtest", Key, 4242);

        Assert.Equal("/Volumes/kwramtest/kw-0123456789abcdef/4242", root);
    }

    /// <summary>A trailing separator in the override must not produce a doubled separator.</summary>
    [Fact]
    public void AnOverrideWithATrailingSeparatorIsTrimmed()
    {
        string root = TestTempRoot.ResolveRoot("/Volumes/kwramtest/", Key, 4242);

        Assert.Equal("/Volumes/kwramtest/kw-0123456789abcdef/4242", root);
    }

    /// <summary>
    ///     The macOS per-user temp directory is longer than the socket budget allows, so with the
    ///     platform pipe still under the root it must be refused rather than silently ignored.
    /// </summary>
    [Fact]
    public void AnOverrideThatCannotCarryTheControlPipeIsRefused()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TestTempRoot.ResolveRoot(DarwinUserTemp, Key, 4242));

        Assert.Contains(TestTempRoot.PipeDirectoryVariable, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     With the control pipe moved out of the root, the same directory is usable: the socket
    ///     limit then constrains the pipe's own directory, not this one.
    /// </summary>
    [Fact]
    public void ALongUnscannedTempDirectoryIsUsableWhenThePipeLivesElsewhere()
    {
        string root = TestTempRoot.ResolveRoot(
            DarwinUserTemp,
            Key,
            4242,
            socketBudgetApplies: false);

        Assert.Equal($"{DarwinUserTemp}/kw-0123456789abcdef/4242", root);
    }

    /// <summary>A relative path would land the root wherever the test happens to run from.</summary>
    [Fact]
    public void ARelativeOverrideIsRefused()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TestTempRoot.ResolveRoot("relative/path", Key, 4242));

        Assert.Contains(TestTempRoot.OverrideVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Failing loudly matters: a silently ignored override would run the suite somewhere else
    ///     and report a number the caller would read as the place they asked for.
    /// </summary>
    [Fact]
    public void AnOverrideTooLongForTheSocketPathIsRefusedRatherThanIgnored()
    {
        string deep = "/Volumes/" + new string('d', TestTempRoot.MaxRootLength);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TestTempRoot.ResolveRoot(deep, Key, 4242));

        Assert.Contains(TestTempRoot.OverrideVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains(TestTempRoot.PipeDirectoryVariable, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A checkout key too short for the default form falls back to the 8-character one instead of
    ///     failing on the slice: the short form still resolves a root that fits the socket budget.
    /// </summary>
    [Fact]
    public void ShortCheckoutKeyUsesShortKeyFallback()
    {
        string root = TestTempRoot.ResolveRoot("/tmp", "abcdef12", 4242);

        Assert.Equal("/tmp/kw-abcdef12/4242", root);
    }

    /// <summary>A checkout key shorter than the minimum length throws <see cref="InvalidOperationException"/>.</summary>
    [Fact]
    public void TooShortCheckoutKeyThrowsInvalidOperation()
    {
        _ = Assert.Throws<InvalidOperationException>(
            () => TestTempRoot.ResolveRoot("/tmp", "abc", 4242));
    }
}
