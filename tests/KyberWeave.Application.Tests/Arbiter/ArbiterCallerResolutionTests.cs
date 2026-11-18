using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Which caller the Arbiter attributes a request to: the harness's own identity outranks the rendered one, which
/// outranks the one a header implies, and a lens or planner header maps to its role. Pure rules over the inputs.
/// </summary>
public sealed class ArbiterCallerResolutionTests
{
    [Fact]
    public void CallerResolution_RanksHarnessAboveRenderedAboveHeaderAboveNone()
    {
        Dictionary<string, string?> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PLAN_FILE"] = "docs/plans/p.md",
        };

        CallerResolution none = CallerResolver.Resolve();
        CallerResolution header = CallerResolver.Resolve(headers: headers);
        CallerResolution rendered = CallerResolver.Resolve(renderedCaller: "conductor", headers: headers);
        CallerResolution harness = CallerResolver.Resolve(
            harnessCaller: "product-owner",
            renderedCaller: "conductor",
            headers: headers);

        Assert.Equal(CallerSource.None, none.Source);
        Assert.Null(none.Caller);
        Assert.Equal(CallerSource.Header, header.Source);
        Assert.Equal("conductor", header.Caller);
        Assert.Equal(CallerSource.Rendered, rendered.Source);
        Assert.Equal("conductor", rendered.Caller);
        Assert.Equal(CallerSource.Harness, harness.Source);
        Assert.Equal("product-owner", harness.Caller);
    }

    [Fact]
    public void CallerResolution_InfersCodeReviewerFromLensHeadersAndAssertsServeCaller()
    {
        Dictionary<string, string?> lensHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LENS"] = "di-composition",
        };
        CallerResolution lens = CallerResolver.Resolve(headers: lensHeaders);
        Assert.Equal(CallerSource.Header, lens.Source);
        Assert.Equal("code-reviewer", lens.Caller);

        Dictionary<string, string?> plannerHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TARGET"] = " architect ",
        };
        CallerResolution planner = CallerResolver.Resolve(headers: plannerHeaders);
        Assert.Equal(CallerSource.Header, planner.Source);
        Assert.Equal("conductor", planner.Caller);

        Dictionary<string, string?> lookalikeHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["TARGET"] = "release-planner-tool",
        };
        CallerResolution lookalike = CallerResolver.Resolve(headers: lookalikeHeaders);
        Assert.Equal(CallerSource.None, lookalike.Source);

        Dictionary<string, string?> markerOnly = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MARKER"] = "arbiter",
        };
        CallerResolution unidentified = CallerResolver.Resolve(headers: markerOnly);
        Assert.Equal(CallerSource.None, unidentified.Source);

        CallerResolution asserted = CallerResolver.Resolve(
            harnessCaller: "product-owner",
            assertedCaller: "serve");
        Assert.Equal(CallerSource.Asserted, asserted.Source);
        Assert.Equal("serve", asserted.Caller);
    }
}
