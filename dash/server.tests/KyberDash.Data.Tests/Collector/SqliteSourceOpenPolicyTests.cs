using KyberDash.Data.Collector.Sources;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>Pins the pure open-and-fallback decision for a harness SQLite source (plan decision D4).</summary>
public class SqliteSourceOpenPolicyTests
{
    private const long CopyCap = 1_000;

    [Fact]
    public void Decide_PlainOpenSucceeded_OpensWithoutFallback()
    {
        SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(SqliteOpenFailure.None, 500, 500, CopyCap);

        Assert.Equal(SqliteOpenActionKind.Open, action.Kind);
        Assert.Null(action.ProblemCode);
    }

    [Theory]
    [InlineData(SqliteOpenFailure.ReadOnlyDirectory)]
    [InlineData(SqliteOpenFailure.MissingSidecar)]
    [InlineData(SqliteOpenFailure.Busy)]
    public void Decide_RecoverableFailureWithEmptyWal_OpensImmutable(SqliteOpenFailure failure)
    {
        SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(failure, 0, 100, CopyCap);

        Assert.Equal(SqliteOpenActionKind.Immutable, action.Kind);
        Assert.Null(action.ProblemCode);
    }

    [Theory]
    [InlineData(SqliteOpenFailure.ReadOnlyDirectory)]
    [InlineData(SqliteOpenFailure.MissingSidecar)]
    [InlineData(SqliteOpenFailure.Busy)]
    public void Decide_RecoverableFailureWithWalBytes_CopiesToCache(SqliteOpenFailure failure)
    {
        SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(failure, 10, 100, CopyCap);

        Assert.Equal(SqliteOpenActionKind.CacheCopy, action.Kind);
        Assert.Null(action.ProblemCode);
    }

    [Fact]
    public void Decide_DatabasePlusWalExactlyAtCap_StillCopies()
    {
        SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(SqliteOpenFailure.Busy, 400, 600, CopyCap);

        Assert.Equal(SqliteOpenActionKind.CacheCopy, action.Kind);
    }

    [Fact]
    public void Decide_DatabasePlusWalOverCap_RefusesWithCopyCapCode()
    {
        SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(SqliteOpenFailure.Busy, 401, 600, CopyCap);

        Assert.Equal(SqliteOpenActionKind.Refuse, action.Kind);
        Assert.Equal("SQLITE_COPY_CAP_EXCEEDED", action.ProblemCode);
        Assert.Equal(SqliteSourceOpenPolicy.CopyCapExceededCode, action.ProblemCode);
    }

    [Fact]
    public void Decide_EmptyWalOverCap_StillOpensImmutableBecauseNothingIsCopied()
    {
        SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(SqliteOpenFailure.ReadOnlyDirectory, 0, CopyCap * 10, CopyCap);

        Assert.Equal(SqliteOpenActionKind.Immutable, action.Kind);
    }

    [Theory]
    [InlineData(SqliteOpenFailure.Missing)]
    [InlineData(SqliteOpenFailure.PermissionDenied)]
    public void Decide_MissingOrPermissionDenied_RefusesAsUnreadableWithoutCopying(SqliteOpenFailure failure)
    {
        SqliteOpenAction action = SqliteSourceOpenPolicy.Decide(failure, 50, 100, CopyCap);

        Assert.Equal(SqliteOpenActionKind.Refuse, action.Kind);
        Assert.Equal("SQLITE_SOURCE_UNREADABLE", action.ProblemCode);
    }

    [Fact]
    public void Decide_UndefinedFailureValue_ThrowsRatherThanGuessing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqliteSourceOpenPolicy.Decide((SqliteOpenFailure)99, 0, 0, CopyCap));
    }

    [Fact]
    public void ProblemCodes_AreTheStableValues()
    {
        Assert.Equal("SQLITE_SOURCE_UNREADABLE", SqliteSourceOpenPolicy.SourceUnreadableCode);
        Assert.Equal("SQLITE_SOURCE_SCHEMA_MISSING", SqliteSourceOpenPolicy.SchemaMissingCode);
        Assert.Equal("SQLITE_EXPORT_CAP_EXCEEDED", SqliteSourceOpenPolicy.ExportCapExceededCode);
        Assert.Equal("SQLITE_COPY_CAP_EXCEEDED", SqliteSourceOpenPolicy.CopyCapExceededCode);
    }

    [Fact]
    public void Limits_DefaultsMatchTheDecision()
    {
        HarnessSqliteLimits limits = new();

        Assert.Equal(67_108_864L, limits.ExportCapBytes);
        Assert.Equal(4_294_967_296L, limits.CopyCapBytes);
        Assert.Equal(30, limits.CommandTimeoutSeconds);
        Assert.Equal(1000, limits.BusyTimeoutMilliseconds);
    }
}
