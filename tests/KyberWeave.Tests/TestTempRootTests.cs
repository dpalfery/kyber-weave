using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Pins the shape and the budget of the test temp root. The redirect itself runs once, in a
///     module initializer, so the rule it applies is tested here as a pure function.
/// </summary>
public sealed class TestTempRootTests
{
    private const string Key = "0123456789abcdef";

    /// <summary>Unset means today's behaviour: a short per-checkout, per-process root under /tmp.</summary>
    [Fact]
    public void NoOverrideKeepsTheDefaultRootUnderTheSystemTempDirectory()
    {
        string root = TestTempRoot.ResolveRoot(null, Key, 4242);

        Assert.Equal("/tmp/kw-0123456789abcdef/4242", root);
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
        const string darwinUserTemp = "/var/folders/gg/jnm7dkgs7w1dhkb3j8wm4gg00000gn/T";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TestTempRoot.ResolveRoot(darwinUserTemp, Key, 4242));

        Assert.Contains(TestTempRoot.PipeDirectoryVariable, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     With the control pipe moved out of the root, the same directory is usable: the socket
    ///     limit then constrains the pipe's own directory, not this one.
    /// </summary>
    [Fact]
    public void ALongUnscannedTempDirectoryIsUsableWhenThePipeLivesElsewhere()
    {
        const string darwinUserTemp = "/var/folders/gg/jnm7dkgs7w1dhkb3j8wm4gg00000gn/T";

        string root = TestTempRoot.ResolveRoot(darwinUserTemp, Key, 4242, socketBudgetApplies: false);

        Assert.Equal($"{darwinUserTemp}/kw-0123456789abcdef/4242", root);
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
}
