using Lib;
using Xunit;

namespace Lib.Tests;

public sealed class CalculatorTests
{
    [Fact]
    public void Add_DoublesTheSumAndAddsTheOffset()
    {
        Calculator calculator = new Calculator(1);

        Assert.Equal(7, calculator.Add(1, 2));
    }
}
