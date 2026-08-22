using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Coverlet reports line-rate as a 0–1 fraction. The collector must scale it to a
/// percent, or a 87.5% report would be graded as 0.875% and every floor would fail.
/// </summary>
public sealed class CoverageCollectorTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadCoberturaScalesLineRateToAPercent()
    {
        string path = Path.Combine(_temp.Path, "coverage.cobertura.xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.875" branch-rate="0.5" version="1.9">
              <packages>
                <package name="KyberWeave.Core" line-rate="0.875">
                  <classes>
                    <class name="A" filename="A.cs" line-rate="1.0" />
                    <class name="B" filename="B.cs" line-rate="0.75" />
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        CoverageResult? coverage = CoverageCollector.ReadCobertura(path);

        Assert.NotNull(coverage);
        Assert.Equal(87.5, coverage.FileLinePercent);
        Assert.Equal(87.5, coverage.ClassLinePercent);
    }
}
